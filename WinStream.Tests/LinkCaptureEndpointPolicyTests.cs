using WinStream.Core.Audio;
using WinStream.Core.Drivers;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkCaptureEndpointPolicyTests
{
    private static readonly RenderEndpointInfo Default =
        new("default-id", "Speakers", IsDefault: true);

    private static readonly RenderEndpointInfo Vad =
        new(
            "vad-id",
            WinStreamVadIdentity.FriendlyName,
            IsDefault: false,
            DeviceInstanceId: $@"{WinStreamVadIdentity.RootHardwareId}\0000");

    [Fact]
    public void Selects_owned_VAD_when_available()
    {
        var selected = LinkCaptureEndpointPolicy.Select([Default, Vad]);

        Assert.Equal("vad-id", selected.EndpointId);
        Assert.True(selected.IsOwnedWinStreamEndpoint);
    }

    [Fact]
    public void Falls_back_to_default_resolution_when_VAD_is_absent()
    {
        var selected = LinkCaptureEndpointPolicy.Select([Default]);

        Assert.Null(selected.EndpointId);
        Assert.False(selected.IsOwnedWinStreamEndpoint);
    }

    [Fact]
    public void Explicit_endpoint_wins_and_only_exact_VAD_name_is_owned()
    {
        var selected = LinkCaptureEndpointPolicy.Select([Default, Vad], "default-id");
        var imitation = new RenderEndpointInfo(
            "other-id",
            WinStreamVadIdentity.FriendlyName,
            IsDefault: false);

        Assert.Equal("default-id", selected.EndpointId);
        Assert.False(selected.IsOwnedWinStreamEndpoint);
        Assert.False(LinkCaptureEndpointPolicy.IsOwned(imitation));
    }

    [Fact]
    public void Friendly_name_candidate_is_preferred_but_not_trusted_for_SLA()
    {
        var imitation = new RenderEndpointInfo(
            "other-id",
            WinStreamVadIdentity.FriendlyName,
            IsDefault: false);

        var selected = LinkCaptureEndpointPolicy.Select([Default, imitation]);

        Assert.Equal("other-id", selected.EndpointId);
        Assert.False(selected.IsOwnedWinStreamEndpoint);
    }
}
