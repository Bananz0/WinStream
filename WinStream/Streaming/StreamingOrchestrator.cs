#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Streaming;
using WinStream.Network;

namespace WinStream.Streaming;

public sealed class StreamingOrchestrator : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly PcmFanoutClock _fanoutClock = new();
    private readonly ReconnectBudget _reconnectBudget = new();
    private readonly object _sessionsGate = new();
    private readonly ResilienceMonitor _resilience = new();
    private readonly TimeSpan _silenceDegradeAfter = TimeSpan.FromSeconds(2.5);
    private IAudioSource? _audioSource;
    private DateTimeOffset? _silentSince;
    private CancellationTokenSource? _reconnectCts;
    private SessionState _aggregateState = SessionState.Disconnected;
    private bool _disposed;

    public StreamingOrchestrator()
    {
        _resilience.RecoverRequested += OnRecoverRequested;
    }

    public event EventHandler<SessionStateChanged>? StateChanged;

    public SessionState State => _aggregateState;

    public DeviceInfo? CurrentReceiver =>
        _sessions.Values.Select(entry => entry.Receiver).FirstOrDefault();

    public IReadOnlyList<DeviceInfo> ConnectedReceivers =>
        _sessions.Values.Select(entry => entry.Receiver).ToList();

    public PcmFanoutClock FanoutClock => _fanoutClock;

    public bool EnableAirPlay2Experimental { get; set; }

    public async Task ConnectAsync(
        DeviceInfo receiver,
        IAudioSource audioSource,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(audioSource);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = ReceiverKey(receiver);
            lock (_sessionsGate)
            {
                if (_sessions.ContainsKey(id))
                {
                    return;
                }
            }

            var protocol = ResolveProtocol(receiver);
            EnsureHomogeneousWithExisting(protocol);

            EnsureAudioSource(audioSource);
            if (!_audioSource!.IsCapturing)
            {
                await _audioSource.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            SetAggregate(SessionState.Connecting, $"Connecting {SafeName(receiver)}");
            IAirPlaySession session = protocol == AirPlayProtocolKind.AirPlay2
                ? new AirPlay2Session(receiver, EnableAirPlay2Experimental)
                : new RaopSession(receiver);
            session.StateChanged += OnSessionStateChanged;
            var entry = new SessionEntry(receiver, session, protocol);
            lock (_sessionsGate)
            {
                _sessions[id] = entry;
            }

            try
            {
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                AppLog.Info("stream", $"Connected receiver count={_sessions.Count}");
            }
            catch
            {
                session.StateChanged -= OnSessionStateChanged;
                lock (_sessionsGate)
                {
                    _sessions.Remove(id);
                }

                await session.DisposeAsync().ConfigureAwait(false);
                RefreshAggregate("connect-failed");
                throw;
            }

            RefreshAggregate("connected");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task SetVolumeAsync(
        float volumeDb,
        CancellationToken cancellationToken = default)
    {
        var tasks = _sessions.Values
            .Select(entry => entry.Session.SetVolumeAsync(volumeDb, cancellationToken))
            .ToArray();
        return Task.WhenAll(tasks);
    }

    public async Task DisconnectAsync(
        DeviceInfo? receiver = null,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (receiver is null)
            {
                await DisconnectAllAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RemoveSessionAsync(ReceiverKey(receiver), cancellationToken)
                    .ConfigureAwait(false);
                RefreshAggregate("removed");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resilience.RecoverRequested -= OnRecoverRequested;
        _resilience.Dispose();
        CancelReconnectLoop();
        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private void EnsureAudioSource(IAudioSource audioSource)
    {
        if (_audioSource is null)
        {
            _audioSource = audioSource;
            _audioSource.FrameAvailable += OnFrameAvailable;
            _audioSource.DeviceInvalidated += OnDeviceInvalidated;
            _audioSource.CaptureFailed += OnCaptureFailed;
            _fanoutClock.Reset();
            return;
        }

        if (!ReferenceEquals(_audioSource, audioSource))
        {
            throw new InvalidOperationException(
                "All multi-room sessions must share the same capture source.");
        }
    }

    private async Task DisconnectAllAsync(CancellationToken cancellationToken)
    {
        CancelReconnectLoop();
        _reconnectBudget.Clear();
        SetAggregate(SessionState.Disconnecting, "Disconnecting all receivers");
        foreach (var key in _sessions.Keys.ToList())
        {
            await RemoveSessionAsync(key, cancellationToken).ConfigureAwait(false);
        }

        DetachAudioSource();
        SetAggregate(SessionState.Disconnected);
    }

    private async Task RemoveSessionAsync(string key, CancellationToken cancellationToken)
    {
        SessionEntry entry;
        lock (_sessionsGate)
        {
            if (!_sessions.Remove(key, out entry!))
            {
                return;
            }
        }

        entry.Session.StateChanged -= OnSessionStateChanged;
        try
        {
            await entry.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }

        await entry.Session.DisposeAsync().ConfigureAwait(false);
    }

    private void DetachAudioSource()
    {
        if (_audioSource is null)
        {
            return;
        }

        _audioSource.FrameAvailable -= OnFrameAvailable;
        _audioSource.DeviceInvalidated -= OnDeviceInvalidated;
        _audioSource.CaptureFailed -= OnCaptureFailed;
        _audioSource = null;
        _silentSince = null;
    }

    private void OnFrameAvailable(object? sender, AudioFrame frame)
    {
        var frames = EstimateOutputFrames(frame);
        var tick = _fanoutClock.Advance(frames);
        SessionEntry[] snapshot;
        lock (_sessionsGate)
        {
            snapshot = _sessions.Values.ToArray();
        }

        foreach (var entry in snapshot)
        {
            if (entry.Session.State is SessionState.Streaming or SessionState.Degraded)
            {
                entry.Session.SubmitPcm(frame.Pcm, frame.Format, tick.Timestamp);
            }
        }

        UpdateSilenceWatchdog();
    }

    private static uint EstimateOutputFrames(AudioFrame frame)
    {
        if (frame.Format.Channels <= 0 || frame.Format.BitsPerSample <= 0)
        {
            return 0;
        }

        var bytesPerFrame = frame.Format.Channels * (frame.Format.BitsPerSample / 8);
        if (bytesPerFrame <= 0)
        {
            return 0;
        }

        var sourceFrames = (uint)(frame.Pcm.Length / bytesPerFrame);
        if (frame.Format.SampleRate == 44100)
        {
            return sourceFrames;
        }

        return (uint)Math.Max(
            1,
            sourceFrames * 44100L / Math.Max(1, frame.Format.SampleRate));
    }

    private void UpdateSilenceWatchdog()
    {
        if (_audioSource is null || _sessions.Count == 0)
        {
            return;
        }

        if (_audioSource.IsSilent)
        {
            _silentSince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _silentSince >= _silenceDegradeAfter &&
                State == SessionState.Streaming)
            {
                SetAggregate(
                    SessionState.Degraded,
                    "Capture is silent (possible DRM or muted output).");
            }
        }
        else
        {
            _silentSince = null;
            if (State == SessionState.Degraded &&
                _sessions.Values.All(entry => entry.Session.State == SessionState.Streaming))
            {
                SetAggregate(SessionState.Streaming, "Capture audio restored.");
            }
        }
    }

    private void OnDeviceInvalidated(object? sender, EventArgs e)
    {
        AppLog.Warn("capture", "Capture device invalidated.");
        _ = BeginReconnectAsync("capture-device-invalidated");
    }

    private void OnCaptureFailed(object? sender, Exception e)
    {
        AppLog.Error("capture", $"Capture failed: {e.GetType().Name}");
        _ = BeginReconnectAsync("capture-failed");
    }

    private void OnRecoverRequested(object? sender, string reason)
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        if (reason is "network-lost" or "power-suspend")
        {
            SetAggregate(SessionState.Reconnecting, reason);
            return;
        }

        _ = BeginReconnectAsync(reason);
    }

    private async Task BeginReconnectAsync(string reason)
    {
        if (_disposed || _sessions.Count == 0)
        {
            return;
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_reconnectCts is not null)
            {
                return;
            }

            _reconnectBudget.Start();
            SetAggregate(SessionState.Reconnecting, reason);
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;
            _ = Task.Run(() => ReconnectLoopAsync(token), token);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_reconnectBudget.IsExpired)
                {
                    AppLog.Warn("stream", "Reconnect budget exhausted.");
                    await FailAllAsync("Reconnect timed out after 30s.").ConfigureAwait(false);
                    return;
                }

                try
                {
                    await AttemptReconnectOnceAsync(cancellationToken).ConfigureAwait(false);
                    if (_sessions.Values.All(entry => entry.Session.State == SessionState.Streaming))
                    {
                        _reconnectBudget.Clear();
                        CancelReconnectLoop();
                        RefreshAggregate("reconnected");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("stream", $"Reconnect attempt failed: {ex.GetType().Name}");
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 4000));
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private async Task AttemptReconnectOnceAsync(CancellationToken cancellationToken)
    {
        if (_audioSource is not null && !_audioSource.IsCapturing)
        {
            await _audioSource.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in _sessions.Values.ToArray())
        {
            if (entry.Session.State == SessionState.Streaming)
            {
                continue;
            }

            try
            {
                await entry.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            entry.Session.StateChanged -= OnSessionStateChanged;
            await entry.Session.DisposeAsync().ConfigureAwait(false);

            IAirPlaySession replacement = entry.Protocol == AirPlayProtocolKind.AirPlay2
                ? new AirPlay2Session(entry.Receiver, EnableAirPlay2Experimental)
                : new RaopSession(entry.Receiver);
            replacement.StateChanged += OnSessionStateChanged;
            entry.Session = replacement;
            await replacement.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private AirPlayProtocolKind ResolveProtocol(DeviceInfo receiver)
    {
        var classic = AirPlayCapability.SupportsClassicRaop(receiver.EncryptionTypes);
        var ap2 = AirPlayCapability.SupportsAirPlay2(
            !string.IsNullOrWhiteSpace(receiver.PublicCUAirPlayPairingIdentity),
            receiver.Features,
            receiver.AirPlayVersion);
        var preferred = AirPlayCapability.PreferredProtocol(
            classic,
            ap2,
            EnableAirPlay2Experimental);
        if (preferred == AirPlayProtocolKind.Unknown)
        {
            throw new InvalidOperationException(
                "Receiver does not advertise a supported AirPlay audio protocol.");
        }

        if (preferred == AirPlayProtocolKind.AirPlay2)
        {
            if (!EnableAirPlay2Experimental)
            {
                throw new InvalidOperationException(
                    "This receiver requires AirPlay 2. " +
                    "Enable the experimental AirPlay 2 gate in settings. " +
                    "On a Mac, also set AirPlay Receiver to allow Everyone (or anyone on the same network).");
            }

            // Gate on: still surface the stub failure with a clear message upstream.
            return AirPlayProtocolKind.AirPlay2;
        }

        return preferred;
    }

    private void EnsureHomogeneousWithExisting(AirPlayProtocolKind incoming)
    {
        var protocols = _sessions.Values
            .Select(entry => entry.Protocol)
            .Append(incoming);
        AirPlayCapability.EnsureHomogeneousSelection(protocols);
    }

    private async Task FailAllAsync(string reason)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelReconnectLoop();
            _reconnectBudget.Clear();
            foreach (var key in _sessions.Keys.ToList())
            {
                await RemoveSessionAsync(key, CancellationToken.None).ConfigureAwait(false);
            }

            DetachAudioSource();
            SetAggregate(SessionState.Disconnected, reason);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void CancelReconnectLoop()
    {
        try
        {
            _reconnectCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }

    private void OnSessionStateChanged(object? sender, SessionStateChanged change)
    {
        RefreshAggregate(change.Reason);
    }

    private void RefreshAggregate(string? reason = null)
    {
        var states = _sessions.Values.Select(entry => entry.Session.State).ToList();
        var silentTooLong = _silentSince is not null &&
                            DateTimeOffset.UtcNow - _silentSince >= _silenceDegradeAfter;
        var next = SessionAggregate.Calculate(
            states,
            reconnectInProgress: _reconnectBudget.IsActive && !_reconnectBudget.IsExpired,
            captureSilentTooLong: silentTooLong);
        SetAggregate(
            next,
            reason ?? (next == SessionState.Degraded
                ? "One or more receivers failed or capture is silent."
                : null));
    }

    private void SetAggregate(SessionState state, string? reason = null)
    {
        if (_aggregateState == state)
        {
            return;
        }

        var previous = _aggregateState;
        _aggregateState = state;
        StateChanged?.Invoke(this, new SessionStateChanged(previous, state, reason));
        if (!string.IsNullOrWhiteSpace(reason))
        {
            AppLog.Info("stream", $"State={state}; {reason}");
        }
    }

    private static string ReceiverKey(DeviceInfo receiver) =>
        !string.IsNullOrWhiteSpace(receiver.DeviceID)
            ? receiver.DeviceID
            : $"{receiver.IPAddress}:{receiver.Port}";

    private static string SafeName(DeviceInfo _) => "receiver";

    private sealed class SessionEntry(
        DeviceInfo receiver,
        IAirPlaySession session,
        AirPlayProtocolKind protocol)
    {
        public DeviceInfo Receiver { get; } = receiver;

        public IAirPlaySession Session { get; set; } = session;

        public AirPlayProtocolKind Protocol { get; } = protocol;
    }
}
