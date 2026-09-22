using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class QualityApplyGateTests
{
    [Fact]
    public void Begins_immediately_when_the_aggregate_is_idle()
    {
        var gate = new QualityApplyGate();

        Assert.True(gate.TryBegin(aggregateBusy: false));
        Assert.False(gate.HasPending);
    }

    [Fact]
    public void Records_a_change_that_arrives_while_busy_instead_of_dropping_it()
    {
        var gate = new QualityApplyGate();

        Assert.False(gate.TryBegin(aggregateBusy: true));
        Assert.True(gate.HasPending);
    }

    [Fact]
    public void Repeats_once_when_a_change_lands_mid_pass()
    {
        var gate = new QualityApplyGate();
        gate.TryBegin(aggregateBusy: false);

        // The pass itself holds the aggregate, so a combo change during it is deferred.
        gate.TryBegin(aggregateBusy: true);

        Assert.True(gate.ShouldRepeat());
        Assert.False(gate.HasPending);
    }

    [Fact]
    public void Does_not_repeat_when_nothing_arrived()
    {
        var gate = new QualityApplyGate();
        gate.TryBegin(aggregateBusy: false);

        Assert.False(gate.ShouldRepeat());
    }

    [Fact]
    public void Collapses_several_changes_during_one_pass_into_a_single_replay()
    {
        var gate = new QualityApplyGate();
        gate.TryBegin(aggregateBusy: false);

        gate.TryBegin(aggregateBusy: true);
        gate.TryBegin(aggregateBusy: true);
        gate.TryBegin(aggregateBusy: true);

        Assert.True(gate.ShouldRepeat());
        Assert.False(gate.ShouldRepeat());
    }

    [Fact]
    public void Begin_clears_a_stale_pending_flag()
    {
        var gate = new QualityApplyGate();
        gate.TryBegin(aggregateBusy: true);

        Assert.True(gate.TryBegin(aggregateBusy: false));
        Assert.False(gate.HasPending);
    }

    [Fact]
    public void Clear_drops_the_replay_after_a_failed_apply()
    {
        var gate = new QualityApplyGate();
        gate.TryBegin(aggregateBusy: false);
        gate.TryBegin(aggregateBusy: true);

        gate.Clear();

        Assert.False(gate.HasPending);
        Assert.False(gate.ShouldRepeat());
    }
}
