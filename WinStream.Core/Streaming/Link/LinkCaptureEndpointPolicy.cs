using WinStream.Core.Audio;
using WinStream.Core.Drivers;

namespace WinStream.Core.Streaming.Link;

public readonly record struct LinkCaptureEndpointSelection(
    string? EndpointId,
    bool IsOwnedWinStreamEndpoint);

/// <summary>Selects the owned VAD for Link without changing AirPlay endpoint selection.</summary>
public static class LinkCaptureEndpointPolicy
{
    public static LinkCaptureEndpointSelection Select(
        IReadOnlyList<RenderEndpointInfo> endpoints,
        string? explicitEndpointId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (!string.IsNullOrWhiteSpace(explicitEndpointId))
        {
            var explicitEndpoint = endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Id, explicitEndpointId, StringComparison.OrdinalIgnoreCase));
            return new LinkCaptureEndpointSelection(
                explicitEndpointId,
                IsOwned(explicitEndpoint));
        }

        var owned = endpoints.FirstOrDefault(IsOwned);
        var candidate = owned ?? endpoints.FirstOrDefault(endpoint =>
            string.Equals(
                endpoint.FriendlyName,
                WinStreamVadIdentity.FriendlyName,
                StringComparison.OrdinalIgnoreCase));
        return candidate is null
            ? new LinkCaptureEndpointSelection(null, false)
            : new LinkCaptureEndpointSelection(candidate.Id, owned is not null);
    }

    public static bool IsOwned(RenderEndpointInfo? endpoint) =>
        endpoint is not null &&
        !string.IsNullOrWhiteSpace(endpoint.DeviceInstanceId) &&
        endpoint.DeviceInstanceId.StartsWith(
            WinStreamVadIdentity.RootHardwareId,
            StringComparison.OrdinalIgnoreCase);
}
