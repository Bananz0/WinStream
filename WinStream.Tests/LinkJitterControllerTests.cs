using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkJitterControllerTests
{
    [Fact]
    public void Starts_at_3ms_on_ethernet_and_8ms_otherwise()
    {
        Assert.Equal(3, new LinkJitterController(pathIsEthernet: true).TargetMilliseconds);
        Assert.Equal(8, new LinkJitterController(pathIsEthernet: false).TargetMilliseconds);
    }

    [Fact]
    public void Grows_by_2ms_on_pressure_after_grace()
    {
        var c = new LinkJitterController(pathIsEthernet: true);
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        c.MarkStarted(t0);
        Assert.False(c.TryUpdate(hadLateOrUnderrun: true, t0.AddMilliseconds(500)));
        Assert.True(c.TryUpdate(hadLateOrUnderrun: true, t0.AddSeconds(2)));
        Assert.Equal(5, c.TargetMilliseconds);
    }

    [Fact]
    public void CoolDown_blocks_rapid_grows()
    {
        var c = new LinkJitterController(pathIsEthernet: true);
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        c.MarkStarted(t0);
        Assert.True(c.TryUpdate(true, t0.AddSeconds(2)));
        Assert.False(c.TryUpdate(true, t0.AddSeconds(2.5)));
        Assert.Equal(5, c.TargetMilliseconds);
        Assert.True(c.TryUpdate(true, t0.AddSeconds(5)));
        Assert.Equal(7, c.TargetMilliseconds);
    }

    [Fact]
    public void Shrinks_after_clean_window()
    {
        var c = new LinkJitterController(pathIsEthernet: true);
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        c.MarkStarted(t0);
        Assert.True(c.TryUpdate(true, t0.AddSeconds(2)));
        Assert.Equal(5, c.TargetMilliseconds);
        Assert.True(c.TryUpdate(false, t0.AddSeconds(25)));
        Assert.Equal(4, c.TargetMilliseconds);
    }

    [Fact]
    public void Does_not_shrink_below_ethernet_start_floor()
    {
        var c = new LinkJitterController(pathIsEthernet: true);
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        c.MarkStarted(t0);
        Assert.False(c.TryUpdate(false, t0.AddSeconds(30)));
        Assert.Equal(3, c.TargetMilliseconds);
    }

    [Fact]
    public void Does_not_shrink_below_non_ethernet_start_floor()
    {
        var c = new LinkJitterController(pathIsEthernet: false);
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        c.MarkStarted(t0);
        Assert.False(c.TryUpdate(false, t0.AddSeconds(30)));
        Assert.Equal(8, c.TargetMilliseconds);
    }

    [Fact]
    public void Does_not_update_before_started()
    {
        var c = new LinkJitterController();
        Assert.False(c.TryUpdate(true, DateTimeOffset.UtcNow));
        Assert.Equal(3, c.TargetMilliseconds);
    }
}
