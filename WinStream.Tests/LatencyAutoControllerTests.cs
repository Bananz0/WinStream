using WinStream.Core.Persistence;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class LatencyAutoControllerTests
{
    [Fact]
    public void ResetForConnect_Auto_starts_at_66150()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        Assert.Equal(LatencyAutoController.AutoStartFrames, controller.EffectiveFrames);
        Assert.True(controller.IsAutoEnabled);
    }

    [Fact]
    public void ResetForConnect_presets_disable_auto_and_set_fixed_frames()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.LowDelay);
        Assert.Equal(LatencyAutoController.LowDelayFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        Assert.Equal(LatencyAutoController.MostStableFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);
    }

    [Fact]
    public void TryRaise_steps_by_11025_up_to_ceiling()
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
        Assert.Equal(66150u + 11025u, controller.EffectiveFrames);

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
        Assert.Equal(66150u + 22050u, controller.EffectiveFrames);

        // Climb to ceiling
        controller.TryRaise(3, 0, true, false, t0.AddSeconds(80));
        controller.TryRaise(3, 0, true, false, t0.AddSeconds(120));
        Assert.Equal(LatencyAutoController.CeilingFrames, controller.EffectiveFrames);
        Assert.False(controller.TryRaise(3, 0, true, false, t0.AddSeconds(160)));
    }

    [Fact]
    public void TryRaise_is_raise_only_and_ignores_silence_and_startup_grace()
    {
        var controller = new LatencyAutoController();
        var t0 = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(t0);

        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(2))); // grace
        Assert.False(controller.TryRaise(10, 10, true, true, t0.AddSeconds(10))); // silent
        Assert.False(controller.TryRaise(10, 10, false, false, t0.AddSeconds(10))); // not streaming

        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        controller.MarkAudioStarted(t0);
        Assert.False(controller.TryRaise(10, 10, true, false, t0.AddSeconds(10)));
        Assert.Equal(LatencyAutoController.MostStableFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void ResolveFixedFrames_never_below_latency_min()
    {
        Assert.True(LatencyAutoController.LowDelayFrames >= LatencyAutoController.LatencyMinFrames);
        Assert.True(LatencyAutoController.AutoStartFrames <= LatencyAutoController.CeilingFrames);
    }
}
