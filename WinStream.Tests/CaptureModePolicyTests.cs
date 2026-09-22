using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class CaptureModePolicyTests
{
    [Fact]
    public void WantsEventDriven_is_always_on_for_Auto_and_opt_in_for_Extreme()
    {
        Assert.True(CaptureModePolicy.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: false,
            PlaybackResponsiveness.Auto));
        Assert.True(CaptureModePolicy.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: true,
            PlaybackResponsiveness.LabPacket));
        Assert.False(CaptureModePolicy.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: false,
            PlaybackResponsiveness.LabPacket));
        Assert.False(CaptureModePolicy.WantsEventDriven(
            extremeEventDrivenCaptureEnabled: true,
            PlaybackResponsiveness.Experimental));
    }

    [Fact]
    public void ResolveContribution_uses_measured_p95_only_when_event_driven_is_warm()
    {
        Assert.Equal(
            50,
            CaptureModePolicy.ResolveContributionMilliseconds(
                useEventDrivenCapture: false,
                hasMeasuredContribution: true,
                measuredContributionMilliseconds: 10));
        Assert.Equal(
            50,
            CaptureModePolicy.ResolveContributionMilliseconds(
                useEventDrivenCapture: true,
                hasMeasuredContribution: false,
                measuredContributionMilliseconds: 10));
        Assert.Equal(
            10,
            CaptureModePolicy.ResolveContributionMilliseconds(
                useEventDrivenCapture: true,
                hasMeasuredContribution: true,
                measuredContributionMilliseconds: 10));
    }

    [Fact]
    public void ArmsExhaustedPressureBanner_only_at_Extreme_ceiling()
    {
        Assert.False(CaptureModePolicy.ArmsExhaustedPressureBanner(
            ladderExhausted: false,
            isStreaming: true,
            isSilent: false,
            pastStartupGrace: true));
        Assert.True(CaptureModePolicy.ArmsExhaustedPressureBanner(
            ladderExhausted: true,
            isStreaming: true,
            isSilent: false,
            pastStartupGrace: true));
        Assert.False(CaptureModePolicy.ArmsExhaustedPressureBanner(
            ladderExhausted: true,
            isStreaming: true,
            isSilent: true,
            pastStartupGrace: true));
        Assert.False(CaptureModePolicy.ArmsExhaustedPressureBanner(
            ladderExhausted: true,
            isStreaming: false,
            isSilent: false,
            pastStartupGrace: true));
        Assert.False(CaptureModePolicy.ArmsExhaustedPressureBanner(
            ladderExhausted: true,
            isStreaming: true,
            isSilent: false,
            pastStartupGrace: false));
    }
}
