#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Streaming;
using WinStream.Network;

namespace WinStream.Streaming;

public sealed class StreamingOrchestrator : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IAirPlaySession? _session;
    private IAudioSource? _audioSource;
    private DeviceInfo? _receiver;
    private bool _disposed;

    public event EventHandler<SessionStateChanged>? StateChanged;

    public SessionState State => _session?.State ?? SessionState.Disconnected;

    public DeviceInfo? CurrentReceiver => _receiver;

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
            if (_session is not null)
            {
                await DisconnectCurrentAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!audioSource.IsCapturing)
            {
                await audioSource.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            var session = new RaopSession(receiver);
            session.StateChanged += OnSessionStateChanged;
            _session = session;
            _receiver = receiver;
            _audioSource = audioSource;
            audioSource.FrameAvailable += OnFrameAvailable;
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
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
        var session = _session;
        return session is null
            ? Task.CompletedTask
            : session.SetVolumeAsync(volumeDb, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCurrentAsync(cancellationToken).ConfigureAwait(false);
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

        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _disposed = true;
    }

    private async Task DisconnectCurrentAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        var audio = _audioSource;
        _session = null;
        _receiver = null;
        _audioSource = null;
        if (audio is not null)
        {
            audio.FrameAvailable -= OnFrameAvailable;
        }

        session.StateChanged -= OnSessionStateChanged;
        await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        StateChanged?.Invoke(
            this,
            new SessionStateChanged(
                SessionState.Disconnecting,
                SessionState.Disconnected));
    }

    private void OnFrameAvailable(object? sender, AudioFrame frame)
    {
        _session?.SubmitPcm(frame.Pcm, frame.Format);
    }

    private void OnSessionStateChanged(object? sender, SessionStateChanged change)
    {
        StateChanged?.Invoke(this, change);
    }
}
