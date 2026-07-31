using WinStream.Core.Protocol.Raop;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class PacketSendSchedulerTests
{
    private const int TargetSampleRate = 44100;

    [Fact]
    public void First_packet_is_due_immediately()
    {
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 1_000);

        Assert.Equal(0, scheduler.TakeWaitTicks(1_000, AlacEncoder.FramesPerPacket));
    }

    [Fact]
    public void Second_packet_waits_one_packet_period()
    {
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 0);
        scheduler.TakeWaitTicks(0, AlacEncoder.FramesPerPacket);

        Assert.Equal(
            PacketSendScheduler.PacketPeriodTicks,
            scheduler.TakeWaitTicks(0, AlacEncoder.FramesPerPacket));
    }

    [Fact]
    public void Overshooting_every_deadline_does_not_accumulate_drift()
    {
        const int packets = 10_000;
        const int overshootTicks = 5;
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 0);

        long now = 0;
        for (var i = 0; i < packets; i++)
        {
            now += scheduler.TakeWaitTicks(now, AlacEncoder.FramesPerPacket);
            now += overshootTicks;
        }

        // A chain of relative sleeps would finish packets * overshootTicks late.
        // Absolute deadlines absorb it, so only the final overshoot survives. The
        // last call schedules packet index packets - 1, which is when the loop ends.
        var idealTicks =
            (long)(packets - 1) * AlacEncoder.FramesPerPacket * TimeSpan.TicksPerSecond /
            TargetSampleRate;
        Assert.InRange(now - idealTicks, 0, overshootTicks);
    }

    [Fact]
    public void Lateness_within_the_allowance_catches_up_without_reanchoring()
    {
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 0);
        scheduler.TakeWaitTicks(0, AlacEncoder.FramesPerPacket);

        var slightlyLate = PacketSendScheduler.PacketPeriodTicks * 2;

        Assert.Equal(0, scheduler.TakeWaitTicks(slightlyLate, AlacEncoder.FramesPerPacket));
        Assert.Equal(0, scheduler.CatchUpClampCount);
    }

    [Fact]
    public void Falling_far_behind_reanchors_instead_of_draining_the_backlog()
    {
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 0);
        scheduler.TakeWaitTicks(0, AlacEncoder.FramesPerPacket);

        var stalledNow = PacketSendScheduler.PacketPeriodTicks *
            (PacketSendScheduler.MaxCatchUpPackets + 50);

        Assert.Equal(0, scheduler.TakeWaitTicks(stalledNow, AlacEncoder.FramesPerPacket));
        Assert.Equal(1, scheduler.CatchUpClampCount);

        // The timeline restarted at the stall, so the next packet is a full period
        // out rather than part of a back-to-back burst.
        Assert.Equal(
            PacketSendScheduler.PacketPeriodTicks,
            scheduler.TakeWaitTicks(stalledNow, AlacEncoder.FramesPerPacket));
    }

    [Fact]
    public void Partial_chunks_advance_the_timeline_by_their_own_duration()
    {
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 0);

        const int halfPacket = AlacEncoder.FramesPerPacket / 2;
        scheduler.TakeWaitTicks(0, halfPacket);

        var expected = halfPacket * TimeSpan.TicksPerSecond / TargetSampleRate;
        Assert.Equal(expected, scheduler.TakeWaitTicks(0, halfPacket));
    }

    [Fact]
    public void Negative_frame_counts_are_rejected()
    {
        var scheduler = new PacketSendScheduler();
        scheduler.Reset(nowTicks: 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => scheduler.TakeWaitTicks(0, outputFrames: -1));
    }
}
