#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Streaming;
using WinStream.Network;

namespace WinStream.Streaming;

/// <summary>
/// Capability-gated AirPlay 2 adapter. Streaming is disabled until the experimental
/// gate is enabled and pairing/PTP work is completed.
/// </summary>
public sealed class AirPlay2Session : IAirPlaySession
{
    private readonly DeviceInfo _receiver;
    private readonly bool _gateEnabled;
    private readonly SessionStateMachine _stateMachine = new();
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

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stateMachine.TransitionTo(SessionState.Connecting);
        if (!_gateEnabled)
        {
            _stateMachine.TransitionTo(
                SessionState.Failed,
                "AirPlay 2 is disabled. Enable the experimental gate in settings after AP2 validation.");
            throw new InvalidOperationException(
                "AirPlay 2 streaming is capability-gated and currently disabled.");
        }

        AppLog.Warn("ap2", "AirPlay 2 gate enabled but pairing/PTP path is not implemented yet.");
        _stateMachine.TransitionTo(
            SessionState.Failed,
            "AirPlay 2 pairing/PTP path is not implemented yet.");
        throw new NotSupportedException(
            "AirPlay 2 media path is not implemented yet. Use a classic RAOP receiver.");
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State != SessionState.Disconnected)
        {
            _stateMachine.TransitionTo(SessionState.Disconnecting);
            _stateMachine.TransitionTo(SessionState.Disconnected);
        }

        return Task.CompletedTask;
    }

    public void SubmitPcm(
        ReadOnlyMemory<byte> pcm,
        AudioFormat format,
        uint? sharedMediaTimestamp = null)
    {
        // No-op until AP2 media path exists.
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
        _disposed = true;
    }
}
