using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkCaptureOpenerTests
{
    [Fact]
    public void Default_ladder_asks_for_the_sla_budget_before_the_fallback()
    {
        Assert.Equal(
            new[]
            {
                LinkSlaEligibility.MaxCaptureContributionMs,
                LinkSlaEligibility.FallbackCaptureBufferMs
            },
            LinkCaptureOpener.DefaultAttemptsMilliseconds);
    }

    [Fact]
    public void Accepted_short_buffer_is_not_reported_as_a_fallback()
    {
        var attempted = new List<int>();

        var result = LinkCaptureOpener.Open(bufferMs =>
        {
            attempted.Add(bufferMs);
            return $"capture@{bufferMs}";
        });

        Assert.Equal(new[] { LinkSlaEligibility.MaxCaptureContributionMs }, attempted);
        Assert.Equal("capture@3", result.Capture);
        Assert.Equal(LinkSlaEligibility.MaxCaptureContributionMs, result.AcceptedBufferMilliseconds);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void Driver_rejecting_three_milliseconds_falls_back_to_ten()
    {
        var failures = new List<int>();

        var result = LinkCaptureOpener.Open(
            bufferMs => bufferMs == LinkSlaEligibility.MaxCaptureContributionMs
                ? throw new InvalidOperationException("AUDCLNT_E_INVALID_DEVICE_PERIOD")
                : $"capture@{bufferMs}",
            onAttemptFailed: (bufferMs, _) => failures.Add(bufferMs));

        Assert.Equal(new[] { LinkSlaEligibility.MaxCaptureContributionMs }, failures);
        Assert.Equal(LinkSlaEligibility.FallbackCaptureBufferMs, result.AcceptedBufferMilliseconds);
        Assert.True(result.IsFallback);
    }

    [Fact]
    public void Fallback_buffer_can_never_satisfy_the_measured_capture_gate()
    {
        Assert.False(
            LinkSlaEligibility.IsMeasuredCaptureSlaCapable(
                LinkSlaEligibility.FallbackCaptureBufferMs));
    }

    [Fact]
    public void Last_error_surfaces_when_every_attempt_fails()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LinkCaptureOpener.Open<string>(bufferMs =>
                throw new InvalidOperationException($"failed at {bufferMs}")));

        Assert.Equal(
            $"failed at {LinkSlaEligibility.FallbackCaptureBufferMs}",
            error.Message);
    }

    [Fact]
    public void Empty_ladder_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() =>
            LinkCaptureOpener.Open(_ => "capture", Array.Empty<int>()));
    }
}
