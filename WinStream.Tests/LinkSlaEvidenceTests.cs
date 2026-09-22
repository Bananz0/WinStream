using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkSlaEvidenceTests
{
    private static LinkMeasurementEvidence Passing() => new(
        CapturedUtc: DateTimeOffset.UtcNow,
        AverageMilliseconds: 9.1,
        P95Milliseconds: 14.2,
        Underruns: 0,
        SampleCount: 600,
        DurationSeconds: 60,
        PathIsEthernet: true,
        CaptureIsOwnedWinStreamEndpoint: true,
        MeasuredCaptureContributionMs: 3,
        RigCalibrationMilliseconds: 1.24);

    [Fact]
    public void A_clean_wired_run_earns_the_claim()
    {
        Assert.True(LinkSlaEvidence.TryExplainFailure(Passing(), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Absent_evidence_never_passes()
    {
        Assert.False(LinkSlaEvidence.TryExplainFailure(null, out var reason));
        Assert.Equal("no measurement has been recorded", reason);
    }

    [Fact]
    public void Loopback_capture_cannot_be_laundered_through_a_good_run()
    {
        var evidence = Passing() with { CaptureIsOwnedWinStreamEndpoint = false };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Fact]
    public void A_wifi_hop_disqualifies_the_run()
    {
        var evidence = Passing() with { PathIsEthernet = false };

        Assert.False(LinkSlaEvidence.TryExplainFailure(evidence, out var reason));
        Assert.Contains("Wi-Fi", reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(10)]
    public void Capture_contribution_over_three_milliseconds_disqualifies_the_run(int captureMs)
    {
        var evidence = Passing() with { MeasuredCaptureContributionMs = captureMs };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Fact]
    public void An_uncalibrated_rig_reports_its_own_latency_as_ours()
    {
        var evidence = Passing() with { RigCalibrationMilliseconds = null };

        Assert.False(LinkSlaEvidence.TryExplainFailure(evidence, out var reason));
        Assert.Contains("never calibrated", reason);
    }

    [Fact]
    public void A_negative_calibration_offset_is_not_physical()
    {
        var evidence = Passing() with { RigCalibrationMilliseconds = -0.5 };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Fact]
    public void A_zero_calibration_offset_is_accepted_because_it_was_still_measured()
    {
        var evidence = Passing() with { RigCalibrationMilliseconds = 0 };

        Assert.True(LinkSlaEvidence.IsPassing(evidence));
    }

    [Fact]
    public void A_short_soak_is_refused_even_when_the_numbers_look_good()
    {
        var evidence = Passing() with { DurationSeconds = 30 };

        Assert.False(LinkSlaEvidence.TryExplainFailure(evidence, out var reason));
        Assert.Contains("60s minimum", reason);
    }

    [Fact]
    public void Too_few_samples_cannot_support_a_p95()
    {
        var evidence = Passing() with { SampleCount = LinkSlaEvidence.MinSampleCount - 1 };

        Assert.False(LinkSlaEvidence.TryExplainFailure(evidence, out var reason));
        Assert.Contains("p95", reason);
    }

    [Fact]
    public void A_single_underrun_disqualifies_the_run()
    {
        var evidence = Passing() with { Underruns = 1 };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Theory]
    [InlineData(7.9)]
    [InlineData(10.1)]
    public void An_average_outside_the_published_band_disqualifies_the_run(double average)
    {
        var evidence = Passing() with { AverageMilliseconds = average };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Fact]
    public void An_average_faster_than_the_claim_still_fails_because_the_claim_is_a_band()
    {
        // Beating the band means the measurement rig is probably wrong, so it is not
        // silently accepted as a better result.
        var evidence = Passing() with { AverageMilliseconds = 2 };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(25)]
    public void A_p95_at_or_over_the_ceiling_disqualifies_the_run(double p95)
    {
        var evidence = Passing() with { P95Milliseconds = p95 };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }

    [Fact]
    public void A_good_average_cannot_hide_a_bad_tail()
    {
        var evidence = Passing() with { AverageMilliseconds = 8.1, P95Milliseconds = 45 };

        Assert.False(LinkSlaEvidence.IsPassing(evidence));
    }
}
