using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkStatusCopyTests
{
    private static LinkUiContext Streaming(
        LinkCaptureQuality quality,
        int measuredMs = 3,
        bool ethernet = false,
        long underruns = 0,
        bool evidence = false) =>
        new(
            Session: LinkSessionState.Streaming,
            CaptureQuality: quality,
            MeasuredCaptureMilliseconds: measuredMs,
            PathIsEthernet: ethernet,
            UnderrunCount: underruns,
            MeasurementEvidencePasses: evidence);

    [Fact]
    public void Card_hint_never_quotes_the_sla_number()
    {
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, LinkStatusCopy.CardHint);
        Assert.DoesNotContain("ultra-low", LinkStatusCopy.CardHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guarantee", LinkStatusCopy.CardHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Idle_explains_the_claim_without_putting_it_on_the_pill()
    {
        var message = LinkStatusCopy.Idle();

        Assert.Equal("Link idle.", message.Headline);
        Assert.Contains(LinkStatusCopy.SlaPhrase, message.Detail);
        Assert.Contains("streaming works without that", message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Link idle", message.Pill);
        Assert.False(message.ClaimsSla);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Headline);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
    }

    [Fact]
    public void Legacy_loopback_is_a_successful_stream_not_an_error()
    {
        var message = LinkStatusCopy.For(Streaming(LinkCaptureQuality.LegacyLoopback));

        Assert.Contains("Standard capture", message.Headline);
        Assert.Contains("Audio is streaming", message.Detail);
        Assert.Equal(LinkUiTone.Success, message.Tone);
        Assert.False(message.ClaimsSla);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Headline);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
    }

    [Fact]
    public void Measuring_vad_is_progress_not_a_claim()
    {
        var message = LinkStatusCopy.For(Streaming(LinkCaptureQuality.VadMeasuring, measuredMs: 0));

        Assert.Contains("Measuring capture", message.Headline);
        Assert.Equal(LinkUiTone.Progress, message.Tone);
        Assert.False(message.ClaimsSla);
    }

    [Fact]
    public void Capture_within_budget_on_wifi_still_refuses_the_claim()
    {
        var message = LinkStatusCopy.For(
            Streaming(LinkCaptureQuality.VadWithinBudget, ethernet: false));

        Assert.Contains("Wi‑Fi path", message.Headline);
        Assert.Contains("Ethernet-lab only", message.Detail);
        Assert.Contains("audio should still play", message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(message.ClaimsSla);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
    }

    [Fact]
    public void Capable_unproven_path_says_proof_pending_not_eight_to_ten()
    {
        var message = LinkStatusCopy.For(
            Streaming(LinkCaptureQuality.VadWithinBudget, ethernet: true));

        Assert.Contains("Proof pending", message.Headline);
        Assert.Equal("Proof pending", message.Pill);
        Assert.Contains(LinkStatusCopy.SlaPhrase, message.Detail!);
        Assert.False(message.ClaimsSla);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Headline);
    }

    [Fact]
    public void Underruns_downgrade_tone_and_block_the_claim()
    {
        var message = LinkStatusCopy.For(
            Streaming(LinkCaptureQuality.VadWithinBudget, ethernet: true, underruns: 2));

        Assert.Equal(LinkUiTone.Caution, message.Tone);
        Assert.Contains("underrun", message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(message.ClaimsSla);
    }

    [Fact]
    public void Over_budget_capture_admits_streaming_continues()
    {
        var message = LinkStatusCopy.For(
            Streaming(LinkCaptureQuality.VadOverBudget, measuredMs: 8, ethernet: true));

        Assert.Contains("over budget", message.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Streaming continues", message.Detail);
        Assert.Equal(LinkUiTone.Caution, message.Tone);
        Assert.False(message.ClaimsSla);
    }

    [Fact]
    public void Only_eligible_plus_evidence_may_claim_the_sla()
    {
        var message = LinkStatusCopy.For(
            Streaming(
                LinkCaptureQuality.VadWithinBudget,
                measuredMs: 3,
                ethernet: true,
                underruns: 0,
                evidence: true));

        Assert.True(message.ClaimsSla);
        Assert.Contains(LinkStatusCopy.SlaPhrase, message.Headline);
        Assert.Contains(LinkStatusCopy.SlaPhrase, message.Pill);
        Assert.Contains("not a guarantee on every network", message.Detail);
        Assert.Equal(LinkUiTone.Success, message.Tone);
    }

    [Fact]
    public void Evidence_alone_cannot_claim_without_ethernet()
    {
        var message = LinkStatusCopy.For(
            Streaming(
                LinkCaptureQuality.VadWithinBudget,
                ethernet: false,
                evidence: true));

        Assert.False(message.ClaimsSla);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
    }

    [Fact]
    public void Evidence_alone_cannot_claim_on_legacy_capture()
    {
        var message = LinkStatusCopy.For(
            Streaming(
                LinkCaptureQuality.LegacyLoopback,
                ethernet: true,
                evidence: true));

        Assert.False(message.ClaimsSla);
    }

    [Theory]
    [InlineData(LinkConnectStatus.MissingPin, "PIN")]
    [InlineData(LinkConnectStatus.InvalidTarget, "IP")]
    [InlineData(LinkConnectStatus.PinRejected, "rejected")]
    [InlineData(LinkConnectStatus.CaptureFailed, "capture")]
    [InlineData(LinkConnectStatus.TransportFailed, "reach")]
    public void Failures_are_actionable_and_never_claim_sla(
        LinkConnectStatus status,
        string expectedFragment)
    {
        var message = LinkStatusCopy.ForFailure(status, detail: null);

        Assert.Contains(expectedFragment, message.Headline + message.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(message.ClaimsSla);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Headline);
        Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
    }

    [Fact]
    public void Empty_scan_tells_the_user_to_start_linkrx_or_type_an_ip()
    {
        var message = LinkStatusCopy.ScanResult(0);

        Assert.Contains("No companions", message.Headline);
        Assert.Contains("LinkRx", message.Detail);
        Assert.False(message.ClaimsSla);
    }

    [Fact]
    public void No_headline_or_pill_leaks_eight_to_ten_without_claims_sla()
    {
        LinkUiMessage[] messages =
        [
            LinkStatusCopy.For(Streaming(LinkCaptureQuality.LegacyLoopback)),
            LinkStatusCopy.For(Streaming(LinkCaptureQuality.VadMeasuring)),
            LinkStatusCopy.For(Streaming(LinkCaptureQuality.VadOverBudget, measuredMs: 9)),
            LinkStatusCopy.For(Streaming(LinkCaptureQuality.VadWithinBudget)),
            LinkStatusCopy.For(Streaming(LinkCaptureQuality.VadWithinBudget, ethernet: true)),
            LinkStatusCopy.Idle(),
            LinkStatusCopy.ScanResult(2),
            LinkStatusCopy.ForFailure(LinkConnectStatus.TransportFailed, "timeout")
        ];

        foreach (var message in messages)
        {
            Assert.False(message.ClaimsSla);
            Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Pill);
            Assert.DoesNotContain(LinkStatusCopy.SlaPhrase, message.Headline);
        }
    }
}
