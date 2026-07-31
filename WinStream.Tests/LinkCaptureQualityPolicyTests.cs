using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkCaptureQualityPolicyTests
{
    [Fact]
    public void Not_streaming_outranks_every_other_fact()
    {
        var quality = LinkCaptureQualityPolicy.Evaluate(
            isStreaming: false,
            isOwnedWinStreamEndpoint: true,
            measuredContributionMilliseconds: 1);

        Assert.Equal(LinkCaptureQuality.NotStreaming, quality);
    }

    [Fact]
    public void Shared_loopback_can_never_claim_the_budget()
    {
        // Even a fast measurement must not upgrade the claim: shared-mode loopback
        // cannot back an 8-10 ms number regardless of what the callbacks showed.
        var quality = LinkCaptureQualityPolicy.Evaluate(
            isStreaming: true,
            isOwnedWinStreamEndpoint: false,
            measuredContributionMilliseconds: 1);

        Assert.Equal(LinkCaptureQuality.LegacyLoopback, quality);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Owned_endpoint_without_a_measurement_is_still_measuring(int measured)
    {
        var quality = LinkCaptureQualityPolicy.Evaluate(
            isStreaming: true,
            isOwnedWinStreamEndpoint: true,
            measuredContributionMilliseconds: measured);

        Assert.Equal(LinkCaptureQuality.VadMeasuring, quality);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(LinkSlaEligibility.MaxCaptureContributionMs)]
    public void Owned_endpoint_within_budget_is_eligible(int measured)
    {
        var quality = LinkCaptureQualityPolicy.Evaluate(
            isStreaming: true,
            isOwnedWinStreamEndpoint: true,
            measuredContributionMilliseconds: measured);

        Assert.Equal(LinkCaptureQuality.VadWithinBudget, quality);
    }

    [Fact]
    public void One_millisecond_over_the_budget_downgrades_the_claim()
    {
        var quality = LinkCaptureQualityPolicy.Evaluate(
            isStreaming: true,
            isOwnedWinStreamEndpoint: true,
            measuredContributionMilliseconds: LinkSlaEligibility.MaxCaptureContributionMs + 1);

        Assert.Equal(LinkCaptureQuality.VadOverBudget, quality);
    }
}
