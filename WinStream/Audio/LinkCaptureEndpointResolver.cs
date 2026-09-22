#nullable enable

using WinStream.Core.Streaming.Link;

namespace WinStream.Audio;

/// <summary>Resolves the owned VAD for Link while leaving AirPlay selection untouched.</summary>
public static class LinkCaptureEndpointResolver
{
    public static LinkCaptureEndpointSelection Resolve(string? explicitEndpointId = null)
    {
        using var enumerator = new RenderEndpointEnumerator();
        return LinkCaptureEndpointPolicy.Select(
            enumerator.ListActiveRenderEndpoints(),
            explicitEndpointId);
    }
}
