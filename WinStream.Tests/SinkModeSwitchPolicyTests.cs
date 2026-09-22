using WinStream.Core;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class SinkModeSwitchPolicyTests
{
    [Fact]
    public void RequiresTeardown_only_when_mode_changes()
    {
        Assert.True(SinkModeSwitchPolicy.RequiresTeardown(SinkMode.AirPlay, SinkMode.Link));
        Assert.True(SinkModeSwitchPolicy.RequiresTeardown(SinkMode.Link, SinkMode.AirPlay));
        Assert.False(SinkModeSwitchPolicy.RequiresTeardown(SinkMode.AirPlay, SinkMode.AirPlay));
    }

    [Fact]
    public void ConfirmMessage_describes_both_XOR_directions()
    {
        Assert.Contains(
            "disconnect AirPlay",
            SinkModeSwitchPolicy.ConfirmMessage(SinkMode.AirPlay, SinkMode.Link));
        Assert.Contains(
            "stop the Link",
            SinkModeSwitchPolicy.ConfirmMessage(SinkMode.Link, SinkMode.AirPlay));
    }

    [Fact]
    public void AutoConnect_mutex_by_mode()
    {
        Assert.True(SinkModeSwitchPolicy.AllowsAirPlayAutoConnect(SinkMode.AirPlay));
        Assert.False(SinkModeSwitchPolicy.AllowsAirPlayAutoConnect(SinkMode.Link));
        Assert.True(SinkModeSwitchPolicy.AllowsLinkAutoConnect(SinkMode.Link));
        Assert.False(SinkModeSwitchPolicy.AllowsLinkAutoConnect(SinkMode.AirPlay));
    }
}
