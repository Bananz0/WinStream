using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class LatencyPressureEvaluationTests
{
    private static readonly DateTimeOffset T0 =
        DateTimeOffset.Parse("2026-07-31T12:00:00Z");

    [Fact]
    public void TryMarkAudioStarted_marks_once_on_first_non_silent_sample()
    {
        var latency = new LatencyAutoController();
        var marked = false;

        Assert.True(LatencyPressureEvaluation.TryMarkAudioStarted(
            ref marked,
            isSilent: false,
            T0,
            latency));
        Assert.True(marked);
        Assert.False(LatencyPressureEvaluation.TryMarkAudioStarted(
            ref marked,
            isSilent: false,
            T0.AddSeconds(1),
            latency));
    }

    [Fact]
    public void EvaluateLatencyChange_auto_raises_on_pressure()
    {
        var latency = new LatencyAutoController();
        latency.ResetForConnect(PlaybackResponsiveness.Auto);
        latency.MarkAudioStarted(T0);

        var signals = new LatencyPressureEvaluation.WindowSignals(
            DropDelta: 3,
            SlowDelta: 0,
            ReanchorDelta: 0,
            IsStreaming: true,
            IsSilent: false,
            Now: T0.AddSeconds(6));

        var outcome = LatencyPressureEvaluation.EvaluateLatencyChange(latency, signals);

        Assert.True(outcome.LatencyChanged);
        Assert.Equal("Auto", outcome.ModeLabel);
        Assert.Equal(3520u, outcome.EffectiveFrames);
    }

    [Fact]
    public void EvaluateLatencyChange_auto_raises_on_timeline_reanchor()
    {
        var latency = new LatencyAutoController();
        latency.ResetForConnect(PlaybackResponsiveness.Auto);
        latency.MarkAudioStarted(T0);

        var signals = new LatencyPressureEvaluation.WindowSignals(
            DropDelta: 0,
            SlowDelta: 0,
            ReanchorDelta: 1,
            IsStreaming: true,
            IsSilent: false,
            Now: T0.AddSeconds(6));

        var outcome = LatencyPressureEvaluation.EvaluateLatencyChange(latency, signals);

        Assert.True(outcome.LatencyChanged);
        Assert.Equal(3520u, outcome.EffectiveFrames);
    }

    [Fact]
    public void EvaluateExtremePressureBanner_toggles_at_ceiling_under_pressure()
    {
        var latency = new LatencyAutoController();
        var hysteresis = new ExtremePressureHysteresis();
        latency.ResetForConnect(PlaybackResponsiveness.LabPacket);
        latency.MarkAudioStarted(T0);
        latency.TryRaiseExtreme(3, 0, true, false, T0.AddSeconds(6));
        latency.TryRaiseExtreme(3, 0, true, false, T0.AddSeconds(20));
        Assert.True(latency.IsExtremeLadderExhausted);

        var signals = new LatencyPressureEvaluation.WindowSignals(
            3,
            0,
            0,
            true,
            false,
            T0.AddSeconds(30));

        Assert.Null(LatencyPressureEvaluation.EvaluateExtremePressureBanner(
            latency,
            hysteresis,
            signals));

        var secondWindow = signals with { Now = T0.AddSeconds(32) };
        Assert.True(LatencyPressureEvaluation.EvaluateExtremePressureBanner(
            latency,
            hysteresis,
            secondWindow));
    }
}
