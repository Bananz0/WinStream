using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class PcmFanoutClockTests
{
    [Fact]
    public void Advance_returns_identical_stamp_for_all_consumers_of_a_tick()
    {
        var clock = new PcmFanoutClock(initialTimestamp: 1000);

        var tick = clock.Advance(352);
        var consumerA = tick.Timestamp;
        var consumerB = tick.Timestamp;

        Assert.Equal(1000u, consumerA);
        Assert.Equal(consumerA, consumerB);
        Assert.Equal(1352u, clock.CurrentTimestamp);
    }

    [Fact]
    public void Peek_does_not_advance()
    {
        var clock = new PcmFanoutClock(50);
        Assert.Equal(50u, clock.Peek().Timestamp);
        Assert.Equal(50u, clock.CurrentTimestamp);
    }
}
