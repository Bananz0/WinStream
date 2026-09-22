using WinStream.Core.Audio;

namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Track B WinStream Link media session — sends WSL1 UDP PCM to an owned companion RX.
/// </summary>
public interface ILinkSession : IAsyncDisposable
{
    event EventHandler<LinkSessionStateChanged>? StateChanged;

    LinkSessionState State { get; }

    string RemoteHost { get; }

    int MediaPort { get; }

    long PacketsSent { get; }

    Task ConnectAsync(
        string host,
        int mediaPort = Protocol.Link.Wsl1Constants.DefaultMediaPort,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Queues PCM for WSL1 packetization and UDP send.</summary>
    void SubmitPcm(ReadOnlyMemory<byte> pcm, AudioFormat format, long timestampTicks);
}
