using WinStream.Core.Audio;

namespace WinStream.Core.Protocol.Link;

/// <summary>
/// Sender side of the Link control plane: an authenticated, reliable channel that
/// brackets the UDP media stream and reports receiver health.
/// </summary>
public interface ILinkControlChannel : IAsyncDisposable
{
    Task StartAsync(int mediaPort, AudioFormat format, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when the receiver declines or answers with something unparseable.</summary>
    Task<LinkReceiverTelemetry?> QueryTelemetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Receiver side of the Link control plane.</summary>
public interface ILinkControlHandler
{
    Task OnStartAsync(int mediaPort, AudioFormat format, CancellationToken cancellationToken);

    Task OnStopAsync(CancellationToken cancellationToken);

    LinkReceiverTelemetry GetTelemetry();
}
