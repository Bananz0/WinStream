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
        Assert.Equal(16538u, outcome.EffectiveFrames);
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
        Assert.Equal(16538u, outcome.EffectiveFrames);
    }

    [Fact]
    public void EvaluateLatencyChange_auto_raises_immediately_on_a_reported_stall()
    {
        var latency = new LatencyAutoController();
        latency.ResetForConnect(PlaybackResponsiveness.Auto);
        latency.MarkAudioStarted(T0);

        var signals = new LatencyPressureEvaluation.WindowSignals(
            DropDelta: 0,
            SlowDelta: 0,
            ReanchorDelta: 0,
            IsStreaming: true,
            IsSilent: false,
            Now: T0.AddSeconds(3),
            PtpReseedDelta: 1);

        var outcome = LatencyPressureEvaluation.EvaluateLatencyChange(latency, signals);

        Assert.True(outcome.LatencyChanged);
        Assert.Equal(16538u, outcome.EffectiveFrames);

        // Immediate raises bypass the raise cooldown, so a second stall in the
        // very next (still-open) window can raise again right away.
        var secondSignals = signals with { PtpReseedDelta = 1 };
        var secondOutcome = LatencyPressureEvaluation.EvaluateLatencyChange(latency, secondSignals);

        Assert.True(secondOutcome.LatencyChanged);
        Assert.Equal(22050u, secondOutcome.EffectiveFrames);
    }

    [Fact]
    public void EvaluateLatencyChange_auto_lowers_only_after_a_reported_silence_window()
    {
        var latency = new LatencyAutoController();
        latency.ResetForConnect(PlaybackResponsiveness.Auto);
        latency.MarkAudioStarted(T0);

        latency.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, latency.EffectiveFrames);

        var t = T0.AddSeconds(5);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            var cleanSignals = new LatencyPressureEvaluation.WindowSignals(
                0, 0, 0, true, false, t);
            LatencyPressureEvaluation.EvaluateLatencyChange(latency, cleanSignals);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        var silentSignals = new LatencyPressureEvaluation.WindowSignals(
            0,
            0,
            0,
            IsStreaming: true,
            IsSilent: true,
            Now: t,
            SilentDuration: LatencyAutoController.MinSilenceForLower);

        var outcome = LatencyPressureEvaluation.EvaluateLatencyChange(latency, silentSignals);

        Assert.True(outcome.LatencyChanged);
        Assert.Equal(11025u, outcome.EffectiveFrames);
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
