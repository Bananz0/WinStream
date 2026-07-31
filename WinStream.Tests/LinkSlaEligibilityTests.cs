using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkSlaEligibilityTests
{
    [Fact]
    public void Eligible_when_ethernet_short_capture_zero_underruns()
    {
        Assert.True(LinkSlaEligibility.IsEligible(
            captureContributionMs: 3,
            captureIsOwnedWinStreamEndpoint: true,
            pathIsEthernet: true,
            underrunCount: 0));
    }

    [Fact]
    public void Ineligible_when_not_ethernet()
    {
        Assert.False(LinkSlaEligibility.IsEligible(
            3,
            captureIsOwnedWinStreamEndpoint: true,
            pathIsEthernet: false,
            underrunCount: 0));
    }

    [Fact]
    public void Ineligible_when_capture_above_3ms()
    {
        Assert.False(LinkSlaEligibility.IsEligible(
            10,
            captureIsOwnedWinStreamEndpoint: true,
            pathIsEthernet: true,
            underrunCount: 0));
        Assert.False(LinkSlaEligibility.IsMeasuredCaptureSlaCapable(10));
        Assert.True(LinkSlaEligibility.IsMeasuredCaptureSlaCapable(3));
    }

    [Fact]
    public void Ineligible_when_capture_is_zero()
    {
        Assert.False(LinkSlaEligibility.IsEligible(
            0,
            captureIsOwnedWinStreamEndpoint: true,
            pathIsEthernet: true,
            underrunCount: 0));
        Assert.False(LinkSlaEligibility.IsMeasuredCaptureSlaCapable(0));
    }

    [Fact]
    public void Ineligible_when_underruns()
    {
        Assert.False(LinkSlaEligibility.IsEligible(
            3,
            captureIsOwnedWinStreamEndpoint: true,
            pathIsEthernet: true,
            underrunCount: 1));
    }

    [Fact]
    public void Ineligible_when_capture_endpoint_is_not_owned()
    {
        Assert.False(LinkSlaEligibility.IsEligible(
            3,
            captureIsOwnedWinStreamEndpoint: false,
            pathIsEthernet: true,
            underrunCount: 0));
    }
}
