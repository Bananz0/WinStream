using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Auto-connect copy must name the preferred receiver — otherwise the toggle looks
/// global while the behavior is per-device.
/// </summary>
public class AutoConnectCopyTests
{
    [Fact]
    public void On_description_names_the_receiver()
    {
        Assert.Contains("living-room", AutoConnectCopy.OnDescription("living-room"));
        Assert.Contains("Auto-connects to", AutoConnectCopy.OnDescription("living-room"));
    }

    [Fact]
    public void Off_description_still_names_the_preferred_receiver()
    {
        Assert.Contains("MacBook", AutoConnectCopy.OffDescription("MacBook"));
        Assert.Contains("Preferred:", AutoConnectCopy.OffDescription("MacBook"));
    }

    [Fact]
    public void Empty_state_points_at_the_star()
    {
        Assert.Contains("star", AutoConnectCopy.NoPreferredDescription);
        Assert.Contains("preferred", AutoConnectCopy.NoPreferredDescription);
    }

    [Fact]
    public void Prefer_tooltips_distinguish_pick_from_already_preferred()
    {
        Assert.Contains("auto-connect", AutoConnectCopy.PreferToolTip);
        Assert.Contains("Preferred", AutoConnectCopy.PreferredToolTip);
        Assert.Equal("Preferred", AutoConnectCopy.PreferredBadge);
    }
}
