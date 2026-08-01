#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Audio;
using WinStream.Core;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Streaming;

public sealed class StreamingOrchestrator : IAsyncDisposable
{
    // ~2 s of WASAPI loopback frames. Deeper than the receiver buffer only delays
    // recovery, because anything older than that is discarded as late anyway.
    private const int SendQueueCapacity = 64;

    private readonly string? _senderDeviceId;
    private readonly IPairingCredentialStore _pairingStore;
    private Func<CancellationToken, Task<string?>>? _requestPairingPinAsync;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly PcmFanoutClock _fanoutClock = new();
    private readonly ReconnectBudget _reconnectBudget = new();
    private readonly object _sessionsGate = new();
    private readonly ResilienceMonitor _resilience = new();
    private readonly LatencyAutoController _latency = new();
    private readonly ExtremePressureHysteresis _extremePressure = new();
    private AudioFrameSendPump? _sendPump;
    private HighResolutionWaiter? _waiter;
    private IAudioSource? _audioSource;
    private CancellationTokenSource? _reconnectCts;
    private SessionState _aggregateState = SessionState.Disconnected;
    private PlaybackResponsiveness _responsiveness = PlaybackResponsiveness.Auto;
    private AudioFidelity _fidelity = AudioFidelity.Auto;
    private float _volumeDb;
    private long _dropsAtWindowStart;
    private long _slowAtWindowStart;
    private DateTimeOffset _signalWindowStart = DateTimeOffset.UtcNow;
    private bool _audioStartedMarked;
    private bool _disposed;

    /// <param name="senderDeviceId">
    /// Persisted per-install sender MAC for AirPlay 2 sessions, resolved by the app.
    /// </param>
    public StreamingOrchestrator(
        string? senderDeviceId = null,
        IPairingCredentialStore? pairingStore = null)
    {
        _senderDeviceId = senderDeviceId;
        _pairingStore = pairingStore ?? new PairingCredentialStore();
        _resilience.RecoverRequested += OnRecoverRequested;
    }

    /// <summary>
    /// Supplies the UI callback that collects the AirPlay code shown on the receiver
    /// during first-time persistent pairing. Return <c>null</c> to cancel and fall
    /// back to transient pairing.
    /// </summary>
    public void SetPairingPinPrompt(Func<CancellationToken, Task<string?>>? requestPinAsync) =>
        _requestPairingPinAsync = requestPinAsync;

    public event EventHandler<SessionStateChanged>? StateChanged;

    /// <summary>
    /// Raised when the Extreme pressure warning should appear or disappear. Fires only
    /// on a change, so the handler can bind it straight to the InfoBar.
    /// </summary>
    public event EventHandler<bool>? ExtremePressureChanged;

    public SessionState State => _aggregateState;

    public uint EffectiveLatencyFrames => _latency.EffectiveFrames;

    /// <summary>
    /// The preset the sessions actually run. Only advances on a successful apply, so the
    /// UI can fall back to it when a preset is refused.
    /// </summary>
    public PlaybackResponsiveness Responsiveness => _responsiveness;

    public IReadOnlyList<DeviceInfo> ConnectedReceivers =>
        _sessions.Values.Select(entry => entry.Receiver).ToList();

    /// <summary>
    /// Stores quality prefs for the next connect. Live preset changes go through
    /// <see cref="ApplyStreamingQualityAsync"/>; Auto mid-session raises still use
    /// the controller independently.
    /// </summary>
    public void ConfigureStreamingQuality(
        PlaybackResponsiveness responsiveness,
        AudioFidelity fidelity)
    {
        _responsiveness = responsiveness;
        _fidelity = fidelity;
    }

    /// <summary>
    /// Applies a fidelity preference to live sessions without restarting them.
    /// </summary>
    /// <remarks>
    /// Conversion policy is a property of the PCM buffer, not of SETUP, so unlike
    /// <see cref="PlaybackResponsiveness"/> it needs no renegotiation with the receiver.
    /// </remarks>
    public void SetAudioFidelity(AudioFidelity fidelity)
    {
        _fidelity = fidelity;
        foreach (var entry in _sessions.Values.ToArray())
        {
            entry.Session.SetAudioFidelity(fidelity);
        }
    }

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
            var id = Core.Network.ReceiverKey.For(receiver);
            lock (_sessionsGate)
            {
                if (_sessions.ContainsKey(id))
                {
                    return;
                }
            }

            var protocol = ResolveProtocol(receiver);
            EnsureHomogeneousWithExisting(protocol);
            EnsureSingleAirPlay2Session(protocol);

            EnsureAudioSource(audioSource);
            if (!_audioSource!.IsCapturing)
            {
                await _audioSource.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            SetAggregate(SessionState.Connecting, "Connecting receiver");

            var isFirstSession = false;
            lock (_sessionsGate)
            {
                isFirstSession = _sessions.Count == 0;
            }

            if (LabSessionPolicy.BlocksAdditionalReceiver(_responsiveness, isFirstSession))
            {
                throw new InvalidOperationException(LabSessionPolicy.MultiRoomBlockedMessage);
            }

            if (isFirstSession)
            {
                _latency.ResetForConnect(_responsiveness);
                _audioStartedMarked = false;
                ClearExtremePressure();
                ResetSignalWindowCounters();
            }

            var session = CreateConfiguredSession(receiver, protocol);
            session.StateChanged += OnSessionStateChanged;
            var entry = new SessionEntry(receiver, session, protocol);
            lock (_sessionsGate)
            {
                _sessions[id] = entry;
            }

            try
            {
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                AppLog.Info(
                    "stream",
                    $"Connected receiver count={_sessions.Count} " +
                    $"latencyFrames={_latency.EffectiveFrames} " +
                    $"setupMin={LatencyAutoController.SetupLatencyMin(_latency.EffectiveFrames)} " +
                    $"responsiveness={_responsiveness} fidelity={_fidelity}");
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

    /// <summary>
    /// Applies new quality prefs immediately. Live AirPlay 2 sessions renegotiate
    /// latency only at SETUP, so each session is torn down and rebuilt in place —
    /// the old session is fully disposed first to release the exclusive PTP ports.
    /// </summary>
    public async Task ApplyStreamingQualityAsync(
        PlaybackResponsiveness responsiveness,
        AudioFidelity fidelity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionEntry[] snapshot;
            lock (_sessionsGate)
            {
                snapshot = _sessions.Values.ToArray();
            }

            if (LabSessionPolicy.BlocksQualityApply(responsiveness, snapshot.Length))
            {
                throw new InvalidOperationException(LabSessionPolicy.MultiRoomBlockedMessage);
            }

            _responsiveness = responsiveness;
            _fidelity = fidelity;
            if (snapshot.Length == 0)
            {
                AppLog.Info(
                    "stream",
                    $"Quality stored for next connect responsiveness={responsiveness} fidelity={fidelity}");
                return;
            }

            _latency.ResetForConnect(responsiveness);
            _audioStartedMarked = false;
            ClearExtremePressure();
            ResetSignalWindowCounters();
            SetAggregate(SessionState.Reconnecting, "Applying streaming quality");

            foreach (var entry in snapshot)
            {
                try
                {
                    await RebuildEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The previous session is already disposed, so a half-built entry
                    // would strand the receiver in the connected list.
                    var failed = entry.Session;
                    failed.StateChanged -= OnSessionStateChanged;
                    lock (_sessionsGate)
                    {
                        _sessions.Remove(Core.Network.ReceiverKey.For(entry.Receiver));
                    }

                    await failed.DisposeAsync().ConfigureAwait(false);
                    RefreshAggregate("quality-apply-failed");
                    throw;
                }
            }

            AppLog.Info(
                "stream",
                $"Applied quality latencyFrames={_latency.EffectiveFrames} " +
                $"responsiveness={_responsiveness} fidelity={_fidelity}");
            RefreshAggregate("quality-applied");
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
        _volumeDb = Math.Clamp(volumeDb, -144f, 0f);
        var tasks = _sessions.Values
            .Select(entry => entry.Session.SetVolumeAsync(_volumeDb, cancellationToken))
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
                await RemoveSessionAsync(Core.Network.ReceiverKey.For(receiver), cancellationToken)
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
            _waiter = new HighResolutionWaiter();
            _sendPump = new AudioFrameSendPump(
                SendQueueCapacity,
                DispatchQueuedFrame,
                MmcssHandle.TryRegisterCurrentThread,
                _waiter.WaitUntilDue);
            _sendPump.Start();
            return;
        }

        if (!ReferenceEquals(_audioSource, audioSource))
        {
            throw new InvalidOperationException(
                "All multi-room sessions must share the same capture source.");
        }
    }

    private async Task DetachAudioSourceAsync()
    {
        if (_audioSource is null)
        {
            return;
        }

        _audioSource.FrameAvailable -= OnFrameAvailable;
        _audioSource.DeviceInvalidated -= OnDeviceInvalidated;
        _audioSource.CaptureFailed -= OnCaptureFailed;
        _audioSource = null;

        var pump = _sendPump;
        _sendPump = null;
        if (pump is not null)
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }

        // Only after the worker has stopped: it waits on the timer handle.
        _waiter?.Dispose();
        _waiter = null;
    }

    private void OnFrameAvailable(object? sender, AudioFrame frame)
    {
        // Capture thread: enqueue only. Encode/encrypt runs on the send pump.
        _sendPump?.Enqueue(frame);
    }

    private void DispatchQueuedFrame(AudioFrame frame)
    {
        // Advance by the same 44.1 kHz stereo frame count PcmPacketBuffer will
        // emit, not the capture buffer length — otherwise shared timestamps drift
        // from packetized RTP after resample.
        var frames = PcmPacketBuffer.EstimateOutputFrames(frame.Pcm.Length, frame.Format);
        var stamp = _fanoutClock.Advance(frames);
        SessionEntry[] snapshot;
        lock (_sessionsGate)
        {
            snapshot = _sessions.Values.ToArray();
        }

        foreach (var entry in snapshot)
        {
            // Read the session once: reconnect can swap it mid-loop, and submitting to
            // the replaced instance would touch a disposed session.
            var session = entry.Session;
            if (session.State is not (SessionState.Streaming or SessionState.Degraded))
            {
                continue;
            }

            try
            {
                session.SubmitPcm(frame.Pcm, frame.Format, stamp);
            }
            catch (Exception ex)
            {
                AppLog.Error(
                    "stream",
                    $"SubmitPcm failed for {entry.Receiver.DisplayName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var pump = _sendPump;
        var drops = pump?.QueueDropCount ?? 0;
        if (drops > 0 && drops % 25 == 0)
        {
            AppLog.Warn(
                "stream",
                $"PCM queue drop_oldest count={drops} depth={pump?.QueueDepth ?? 0}");
        }

        EvaluatePressureWindow(pump);
    }

    /// <summary>
    /// Closes one signal window and routes its pressure: Auto and Extreme climb
    /// their ladders; Extreme only surfaces the InfoBar once that ladder is exhausted.
    /// </summary>
    private void EvaluatePressureWindow(AudioFrameSendPump? pump)
    {
        if (pump is null || _sessions.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isSilent = _audioSource?.IsSilent == true;
        if (!_audioStartedMarked && !isSilent)
        {
            _latency.MarkAudioStarted(now);
            _audioStartedMarked = true;
            ResetSignalWindowCounters();
            return;
        }

        if (now - _signalWindowStart < LatencyAutoController.SignalWindow)
        {
            return;
        }

        var dropDelta = pump.QueueDropCount - _dropsAtWindowStart;
        var slowDelta = pump.SlowSendCount - _slowAtWindowStart;
        var isStreaming =
            State is SessionState.Streaming or SessionState.Degraded;

        if (_latency.IsAutoEnabled || _latency.IsExtremeRaiseEnabled)
        {
            if (_latency.TryRaise(dropDelta, slowDelta, isStreaming, isSilent, now))
            {
                ApplyLatencyToAllSessions(_latency.EffectiveFrames);
                var kind = _latency.IsExtremeRaiseEnabled ? "Extreme" : "Auto";
                AppLog.Info(
                    "stream",
                    $"{kind} latency raised to {_latency.EffectiveFrames} frames " +
                    $"(~{_latency.EffectiveFrames / 44.1:0} ms) " +
                    $"drops={dropDelta} slowSends={slowDelta}");

                // A successful mid-ladder raise clears any stale exhausted banner.
                if (_latency.IsExtremeRaiseEnabled && !_latency.IsExtremeLadderExhausted)
                {
                    ClearExtremePressure();
                }
            }
        }

        if (_latency.IsExtremeRaiseEnabled)
        {
            UpdateExtremePressure(dropDelta, slowDelta, isStreaming, isSilent, now);
        }

        ResetSignalWindowCounters();
    }

    private void UpdateExtremePressure(
        long dropDelta,
        long slowDelta,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset now)
    {
        // Mid-ladder raises are silent. The banner only arms at the Extreme ceiling.
        var eligible = ExtremeCaptureExperiment.ArmsExhaustedPressureBanner(
            Responsiveness,
            _latency.IsExtremeLadderExhausted,
            isStreaming,
            isSilent,
            _latency.IsPastStartupGrace(now));

        var pressure = eligible && LatencyAutoController.HasPressure(dropDelta, slowDelta);
        var wasVisible = _extremePressure.IsWarningVisible;
        var visible = _extremePressure.ObserveWindow(pressure);
        if (visible == wasVisible)
        {
            return;
        }

        if (visible)
        {
            AppLog.Warn(
                "stream",
                $"Extreme ladder exhausted under pressure drops={dropDelta} slowSends={slowDelta}");
        }

        ExtremePressureChanged?.Invoke(this, visible);
    }

    private void ApplyLatencyToAllSessions(uint frames)
    {
        SessionEntry[] snapshot;
        lock (_sessionsGate)
        {
            snapshot = _sessions.Values.ToArray();
        }

        foreach (var entry in snapshot)
        {
            entry.Session.SetEffectiveLatencyFrames(frames);
        }
    }

    private void ClearExtremePressure()
    {
        if (!_extremePressure.IsWarningVisible)
        {
            _extremePressure.Reset();
            return;
        }

        _extremePressure.Reset();
        ExtremePressureChanged?.Invoke(this, false);
    }

    private void ResetSignalWindowCounters()
    {
        _signalWindowStart = DateTimeOffset.UtcNow;
        _dropsAtWindowStart = _sendPump?.QueueDropCount ?? 0;
        _slowAtWindowStart = _sendPump?.SlowSendCount ?? 0;
    }

    private async Task DisconnectAllAsync(CancellationToken cancellationToken)
    {
        CancelReconnectLoop();
        _reconnectBudget.Clear();
        ClearExtremePressure();
        SetAggregate(SessionState.Disconnecting, "Disconnecting all receivers");
        foreach (var key in _sessions.Keys.ToList())
        {
            await RemoveSessionAsync(key, cancellationToken).ConfigureAwait(false);
        }

        await DetachAudioSourceAsync().ConfigureAwait(false);
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

            await RebuildEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces a session in place, preserving its slot in <see cref="_sessions"/>.
    /// The dispose/build/volume/connect order is owned by <see cref="SessionRebuild"/>.
    /// </summary>
    private async Task RebuildEntryAsync(
        SessionEntry entry,
        CancellationToken cancellationToken)
    {
        var retired = entry.Session;
        retired.StateChanged -= OnSessionStateChanged;

        await SessionRebuild.ReplaceAsync(
                retired,
                () =>
                {
                    var replacement = CreateConfiguredSession(entry.Receiver, entry.Protocol);
                    replacement.StateChanged += OnSessionStateChanged;
                    lock (_sessionsGate)
                    {
                        entry.Session = replacement;
                    }

                    return replacement;
                },
                _volumeDb,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static AirPlayProtocolKind ResolveProtocol(DeviceInfo receiver)
    {
        var preferred = AirPlayCapability.PreferredProtocol(receiver);
        if (preferred == AirPlayProtocolKind.Unknown)
        {
            throw new InvalidOperationException(
                "Receiver does not advertise a supported AirPlay audio protocol.");
        }

        return preferred;
    }

    /// <summary>Builds a session already carrying the current shared latency.</summary>
    private IAirPlaySession CreateConfiguredSession(
        DeviceInfo receiver,
        AirPlayProtocolKind protocol)
    {
        IAirPlaySession session = protocol == AirPlayProtocolKind.AirPlay2
            ? new AirPlay2Session(receiver, _senderDeviceId, CreatePairingOptions(receiver))
            : new RaopSession(receiver);
        session.SetEffectiveLatencyFrames(_latency.EffectiveFrames);
        session.SetAudioFidelity(_fidelity);
        return session;
    }

    /// <summary>
    /// The orchestrator owns pairing persistence; the protocol only consumes the
    /// credentials and reports back which ones worked.
    /// </summary>
    private PairingOptions CreatePairingOptions(DeviceInfo receiver)
    {
        var receiverKey = ReceiverKey.For(receiver);
        return new PairingOptions
        {
            StoredCredentials = _pairingStore.TryGet(receiverKey, out var stored) ? stored : null,
            RequestPinAsync = _requestPairingPinAsync,
            OnPaired = credentials => _pairingStore.Save(receiverKey, credentials),
            OnStoredCredentialsRejected = () => _pairingStore.Remove(receiverKey)
        };
    }

    private void EnsureHomogeneousWithExisting(AirPlayProtocolKind incoming)
    {
        var protocols = _sessions.Values
            .Select(entry => entry.Protocol)
            .Append(incoming);
        AirPlayCapability.EnsureHomogeneousSelection(protocols);
    }

    /// <summary>
    /// PTP ports 319/320 are process-exclusive — only one AP2 session can own them.
    /// </summary>
    private void EnsureSingleAirPlay2Session(AirPlayProtocolKind incoming)
    {
        if (incoming != AirPlayProtocolKind.AirPlay2)
        {
            return;
        }

        if (_sessions.Values.Any(entry => entry.Protocol == AirPlayProtocolKind.AirPlay2))
        {
            throw new InvalidOperationException(
                "Only one AirPlay 2 receiver can be connected at a time " +
                "(PTP clock ports 319/320 are exclusive).");
        }
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

            await DetachAudioSourceAsync().ConfigureAwait(false);
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
        var next = SessionAggregate.Calculate(
            states,
            reconnectInProgress: _reconnectBudget.IsActive && !_reconnectBudget.IsExpired);
        SetAggregate(
            next,
            reason ?? (next == SessionState.Degraded
                ? "One or more receivers failed."
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
