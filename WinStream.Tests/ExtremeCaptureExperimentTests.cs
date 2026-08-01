using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class ExtremeCaptureExperimentTests
{
    [Fact]
    public void WantsEventDriven_requires_flag_and_Extreme()
    {
        Assert.True(ExtremeCaptureExperiment.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: true,
            PlaybackResponsiveness.LabPacket));
        Assert.False(ExtremeCaptureExperiment.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: false,
            PlaybackResponsiveness.LabPacket));
        Assert.False(ExtremeCaptureExperiment.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: true,
            PlaybackResponsiveness.Experimental));
        Assert.False(ExtremeCaptureExperiment.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: true,
            PlaybackResponsiveness.Auto));
    }

    [Fact]
    public void ResolveContribution_uses_measured_p95_only_when_experiment_is_warm()
    {
        Assert.Equal(
            50,
            ExtremeCaptureExperiment.ResolveContributionMilliseconds(
                useEventDrivenCapture: false,
                hasMeasuredContribution: true,
                measuredContributionMilliseconds: 10));
        Assert.Equal(
            50,
            ExtremeCaptureExperiment.ResolveContributionMilliseconds(
                useEventDrivenCapture: true,
                hasMeasuredContribution: false,
                measuredContributionMilliseconds: 10));
        Assert.Equal(
            10,
            ExtremeCaptureExperiment.ResolveContributionMilliseconds(
                useEventDrivenCapture: true,
                hasMeasuredContribution: true,
                measuredContributionMilliseconds: 10));
    }

    [Fact]
    public void ArmsExhaustedPressureBanner_only_at_Extreme_ceiling()
    {
        Assert.False(ExtremeCaptureExperiment.ArmsExhaustedPressureBanner(
            PlaybackResponsiveness.LabPacket,
            ladderExhausted: false,
            isStreaming: true,
            isSilent: false,
            pastStartupGrace: true));
        Assert.True(ExtremeCaptureExperiment.ArmsExhaustedPressureBanner(
            PlaybackResponsiveness.LabPacket,
            ladderExhausted: true,
            isStreaming: true,
            isSilent: false,
            pastStartupGrace: true));
        Assert.False(ExtremeCaptureExperiment.ArmsExhaustedPressureBanner(
            PlaybackResponsiveness.LabPacket,
            ladderExhausted: true,
            isStreaming: true,
            isSilent: true,
            pastStartupGrace: true));
        Assert.False(ExtremeCaptureExperiment.ArmsExhaustedPressureBanner(
            PlaybackResponsiveness.Experimental,
            ladderExhausted: true,
            isStreaming: true,
            isSilent: false,
            pastStartupGrace: true));
    }
}
