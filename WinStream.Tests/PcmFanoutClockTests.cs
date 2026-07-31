using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class PcmFanoutClockTests
{
    [Fact]
    public void Advance_returns_identical_stamp_for_all_consumers_of_a_tick()
    {
        var clock = new PcmFanoutClock(initialTimestamp: 1000);

        var stamp = clock.Advance(352);
        Assert.Equal(1000u, stamp);
        Assert.Equal(1352u, clock.CurrentTimestamp);
    }

    [Fact]
    public void Default_constructor_starts_above_typical_latency_window()
    {
        var clock = new PcmFanoutClock();
        Assert.True(clock.CurrentTimestamp > 88_200u);
    }

    [Fact]
    public void Reset_zero_picks_a_safe_non_zero_base()
    {
        var clock = new PcmFanoutClock(100);
        clock.Reset(0);
        Assert.True(clock.CurrentTimestamp > 88_200u);
    }

    [Fact]
    public void Reset_explicit_sets_value()
    {
        var clock = new PcmFanoutClock();
        clock.Reset(42);
        Assert.Equal(42u, clock.CurrentTimestamp);
    }
}
