using WinStream.Core.Audio;

namespace WinStream.Core.Streaming;

public interface IAirPlaySession : IAsyncDisposable
{
    event EventHandler<SessionStateChanged>? StateChanged;

    string ReceiverId { get; }

    SessionState State { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    void SubmitPcm(
        ReadOnlyMemory<byte> pcm,
        AudioFormat format,
        uint? sharedMediaTimestamp = null);

    Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default);
}
