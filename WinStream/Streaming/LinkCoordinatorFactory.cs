#nullable enable

using WinStream.Audio;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Streaming;

/// <summary>
/// Binds the Core Link coordinator to the Windows implementations: VAD-preferred WASAPI
/// capture, WSL1 UDP session, and the TCP PIN handshake.
/// </summary>
public static class LinkCoordinatorFactory
{
    public static LinkConnectionCoordinator Create(ILinkCredentialStore credentials) =>
        new(
            CreateCapture,
            static () => new LinkSession(),
            static async (target, pin, cancellationToken) =>
                await LinkControlClient.ConnectAsync(
                    target.Host,
                    target.ControlPort,
                    pin,
                    cancellationToken),
            credentials);

    private static ILinkCaptureSource CreateCapture(string? explicitEndpointId)
    {
        var selection = LinkCaptureEndpointResolver.Resolve(explicitEndpointId);
        return new LinkWasapiLoopbackSource
        {
            PreferredEndpointId = selection.EndpointId,
            PreferredEndpointIsOwnedWinStreamVad = selection.IsOwnedWinStreamEndpoint
        };
    }
}
