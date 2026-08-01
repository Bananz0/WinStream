using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class LatencyAutoControllerTests
{
    [Fact]
    public void ResetForConnect_Auto_starts_at_11025()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        Assert.Equal(11025u, controller.EffectiveFrames);
        Assert.Equal(LatencyAutoController.AutoStartFrames, controller.EffectiveFrames);
        Assert.True(controller.IsAutoEnabled);
    }

    [Fact]
    public void ResetForConnect_presets_disable_auto_and_set_fixed_frames()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.VeryLow);
        Assert.Equal(LatencyAutoController.VeryLowFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.Experimental);
        Assert.Equal(LatencyAutoController.ExperimentalFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        Assert.Equal(2112u, controller.EffectiveFrames);
        Assert.Equal(LatencyAutoController.LabPacketFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);
        Assert.True(controller.IsExtremeRaiseEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.LowDelay);
        Assert.Equal(LatencyAutoController.LowDelayFrames, controller.EffectiveFrames);

        controller.ResetForConnect(PlaybackResponsiveness.Balanced);
        Assert.Equal(LatencyAutoController.BalancedFrames, controller.EffectiveFrames);

        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        Assert.Equal(LatencyAutoController.MostStableFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void ResolveFixedFrames_matches_documented_constants_for_every_mode()
    {
        Assert.Equal(
            LatencyAutoController.AutoStartFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.Auto));
        Assert.Equal(
            LatencyAutoController.VeryLowFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.VeryLow));
        Assert.Equal(
            LatencyAutoController.ExperimentalFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.Experimental));
        Assert.Equal(
            LatencyAutoController.LabPacketFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.LabPacket));
        Assert.Equal(
            LatencyAutoController.LowDelayFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.LowDelay));
        Assert.Equal(
            LatencyAutoController.BalancedFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.Balanced));
        Assert.Equal(
            LatencyAutoController.MostStableFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.MostStable));
        Assert.Equal(2112u, LatencyAutoController.LabPacketFrames);
        Assert.True(LatencyAutoController.LabPacketFrames >= LatencyAutoController.PacketFloorFrames);
        Assert.Equal(3520u, LatencyAutoController.ExtremeMidFrames);
        Assert.Equal(11025u, LatencyAutoController.ExtremeCeilingFrames);
    }

    [Fact]
    public void Extreme_starts_at_2112_and_raises_through_3520_then_11025()
    {
        var controller = new LatencyAutoController();
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        controller.MarkAudioStarted(t0);

        Assert.Equal(2112u, controller.EffectiveFrames);
        Assert.False(controller.IsExtremeLadderExhausted);

        Assert.True(controller.TryRaise(3, 0, true, false, t0.AddSeconds(6)));
        Assert.Equal(3520u, controller.EffectiveFrames);
        Assert.False(controller.IsExtremeLadderExhausted);

        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(10))); // cool-down

        Assert.True(controller.TryRaise(0, 5, true, false, t0.AddSeconds(20)));
        Assert.Equal(11025u, controller.EffectiveFrames);
        Assert.True(controller.IsExtremeLadderExhausted);

        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(40)));
        Assert.Equal(11025u, controller.EffectiveFrames);
    }

    [Fact]
    public void Extreme_ResetForConnect_returns_to_2112_after_a_climb()
    {
        var controller = new LatencyAutoController();
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        controller.MarkAudioStarted(t0);
        controller.TryRaise(3, 0, true, false, t0.AddSeconds(6));
        controller.TryRaise(3, 0, true, false, t0.AddSeconds(20));
        Assert.Equal(11025u, controller.EffectiveFrames);

        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        Assert.Equal(2112u, controller.EffectiveFrames);
        Assert.False(controller.IsExtremeLadderExhausted);
    }

    [Fact]
    public void Extreme_TryRaise_ignores_silence_startup_and_subthreshold()
    {
        var controller = new LatencyAutoController();
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);

        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(10))); // unmarked

        controller.MarkAudioStarted(t0);
        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(2))); // grace
        Assert.False(controller.TryRaise(10, 10, true, true, t0.AddSeconds(10))); // silent
        Assert.False(controller.TryRaise(10, 10, false, false, t0.AddSeconds(10))); // not streaming
        Assert.False(controller.TryRaise(2, 4, true, false, t0.AddSeconds(10))); // below thresholds
        Assert.Equal(2112u, controller.EffectiveFrames);
    }

    [Fact]
    public void TryRaise_steps_by_11025_up_to_ceiling_from_auto_floor()
    {
        var controller = new LatencyAutoController();
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(t0);

        Assert.True(controller.TryRaise(
            queueDropsInWindow: 3,
            slowSendsInWindow: 0,
            isStreaming: true,
            isSilent: false,
            utcNow: t0.AddSeconds(6)));
        Assert.Equal(11025u + 11025u, controller.EffectiveFrames);

        Assert.False(controller.TryRaise(
            queueDropsInWindow: 10,
            slowSendsInWindow: 10,
            isStreaming: true,
            isSilent: false,
            utcNow: t0.AddSeconds(10))); // cool-down

        Assert.True(controller.TryRaise(
            queueDropsInWindow: 0,
            slowSendsInWindow: 5,
            isStreaming: true,
            isSilent: false,
            utcNow: t0.AddSeconds(40)));
        Assert.Equal(11025u + 22050u, controller.EffectiveFrames);

        for (var i = 0; i < 10; i++)
        {
            controller.TryRaise(3, 0, true, false, t0.AddSeconds(80 + (i * 40)));
        }

        Assert.Equal(LatencyAutoController.CeilingFrames, controller.EffectiveFrames);
        Assert.False(controller.TryRaise(3, 0, true, false, t0.AddSeconds(500)));
    }

    [Fact]
    public void TryRaise_is_raise_only_and_ignores_silence_startup_and_subthreshold()
    {
        var controller = new LatencyAutoController();
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        controller.ResetForConnect(PlaybackResponsiveness.Auto);

        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(10))); // unmarked

        controller.MarkAudioStarted(t0);
        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(2))); // grace
        Assert.False(controller.TryRaise(10, 10, true, true, t0.AddSeconds(10))); // silent
        Assert.False(controller.TryRaise(10, 10, false, false, t0.AddSeconds(10))); // not streaming
        Assert.False(controller.TryRaise(2, 4, true, false, t0.AddSeconds(10))); // below thresholds

        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        controller.MarkAudioStarted(t0);
        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(10)));
        Assert.Equal(LatencyAutoController.MostStableFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void SetupLatencyMin_lab_uses_352_otherwise_folklore_11025()
    {
        Assert.Equal(352u, LatencyAutoController.SetupLatencyMin(352));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(11025));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(22050));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(88200));
    }

    [Fact]
    public void SetupLatencyMax_at_least_88200()
    {
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(352));
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(11025));
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(88200));
    }

    [Fact]
    public void SetupLatencyMax_follows_L_above_the_ceiling()
    {
        var above = 88200u + LatencyAutoController.StepFrames;
        Assert.Equal(above, LatencyAutoController.SetupLatencyMax(above));
    }

    [Fact]
    public void ClampEffectiveFrames_packet_floor()
    {
        Assert.Equal(352u, LatencyAutoController.ClampEffectiveFrames(0));
        Assert.Equal(352u, LatencyAutoController.ClampEffectiveFrames(100));
        Assert.Equal(11025u, LatencyAutoController.ClampEffectiveFrames(11025));
    }
}
