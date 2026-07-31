#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;
using WinStream.Network;

namespace WinStream.Streaming;

/// <summary>
/// AirPlay 2 sender session. Phase 3: HKP + encrypted RTSP through session SETUP.
/// Media (event/RECORD/ALAC) lands in Phase 4.
/// </summary>
public sealed class AirPlay2Session : IAirPlaySession
{
    private readonly DeviceInfo _receiver;
    private readonly bool _gateEnabled;
    private readonly SessionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private EncryptedRtspClient? _control;
    private bool _disposed;

    public AirPlay2Session(DeviceInfo receiver, bool gateEnabled)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _gateEnabled = gateEnabled;
        ReceiverId = !string.IsNullOrWhiteSpace(receiver.DeviceID)
            ? receiver.DeviceID
            : $"{receiver.IPAddress}:{receiver.Port}";
        _stateMachine.StateChanged += (_, change) =>
            StateChanged?.Invoke(this, change);
    }

    public event EventHandler<SessionStateChanged>? StateChanged;

    public string ReceiverId { get; }

    public SessionState State => _stateMachine.State;

    public int EventPort => _control?.EventPort ?? 0;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SessionState.Streaming)
            {
                return;
            }

            _stateMachine.TransitionTo(SessionState.Connecting);
            if (!_gateEnabled)
            {
                _stateMachine.TransitionTo(
                    SessionState.Failed,
                    "AirPlay 2 is disabled. Enable the experimental gate in settings after AP2 validation.");
                throw new InvalidOperationException(
                    "AirPlay 2 streaming is capability-gated and currently disabled.");
            }

            var client = new EncryptedRtspClient(_receiver.IPAddress, _receiver.Port)
            {
                DeviceId = ResolveDeviceId(_receiver),
                LocalIp = "0.0.0.0"
            };
            _control = client;

            try
            {
                await client.ConnectAndPairAsync(cancellationToken).ConfigureAwait(false);
                await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
                await client.SessionSetupAsync(cancellationToken).ConfigureAwait(false);
                AppLog.Info(
                    "ap2",
                    $"Session SETUP ok eventPort={client.EventPort} (media path Phase 4).");
                _stateMachine.TransitionTo(SessionState.Streaming);
            }
            catch (Exception ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                _control = null;
                _stateMachine.TransitionTo(SessionState.Failed, ex.Message);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is SessionState.Disconnected)
            {
                return;
            }

            _stateMachine.TransitionTo(SessionState.Disconnecting);
            if (_control is not null)
            {
                await _control.DisposeAsync().ConfigureAwait(false);
                _control = null;
            }

            _stateMachine.TransitionTo(SessionState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void SubmitPcm(
        ReadOnlyMemory<byte> pcm,
        AudioFormat format,
        uint? sharedMediaTimestamp = null)
    {
        // Media path arrives in Phase 4.
    }

    public Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _disposed = true;
    }

    private static string ResolveDeviceId(DeviceInfo receiver)
    {
        if (!string.IsNullOrWhiteSpace(receiver.DeviceID) &&
            receiver.DeviceID.Contains(':', StringComparison.Ordinal))
        {
            return receiver.DeviceID;
        }

        // Synthetic sender MAC for SETUP; receiver identity is separate.
        return "02:00:00:00:00:01";
    }
}
