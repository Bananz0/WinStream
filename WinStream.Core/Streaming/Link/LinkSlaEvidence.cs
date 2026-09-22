namespace WinStream.Core.Streaming.Link;

/// <summary>
/// A recorded end-to-end measurement run. Produced by the lab measurement harness,
/// never by live session counters — the point is that the badge rests on a committed
/// artifact somebody can re-read, not on whatever the current session happens to see.
/// </summary>
/// <param name="CapturedUtc">When the run finished.</param>
/// <param name="AverageMilliseconds">Mean E2E latency across the run.</param>
/// <param name="P95Milliseconds">95th percentile E2E latency.</param>
/// <param name="Underruns">Receiver underruns observed during the run.</param>
/// <param name="SampleCount">Number of individual latency measurements.</param>
/// <param name="DurationSeconds">Length of the soak.</param>
/// <param name="PathIsEthernet">True only when no Wi-Fi hop carried media.</param>
/// <param name="CaptureIsOwnedWinStreamEndpoint">True only for the WinStream VAD.</param>
/// <param name="MeasuredCaptureContributionMs">Measured capture callback p95.</param>
/// <param name="RigCalibrationMilliseconds">
/// The measurement rig's own latency, measured with the system under test removed and
/// already subtracted from the reported figures. Null means the rig was never
/// calibrated, which makes every number here the rig's latency plus ours.
/// </param>
public sealed record LinkMeasurementEvidence(
    DateTimeOffset CapturedUtc,
    double AverageMilliseconds,
    double P95Milliseconds,
    long Underruns,
    int SampleCount,
    int DurationSeconds,
    bool PathIsEthernet,
    bool CaptureIsOwnedWinStreamEndpoint,
    int MeasuredCaptureContributionMs,
    double? RigCalibrationMilliseconds);

/// <summary>
/// Decides whether a recorded measurement earns the 8–10 ms claim.
/// </summary>
/// <remarks>
/// Separate from <see cref="LinkSlaEligibility"/> on purpose. Eligibility answers
/// "is this session's configuration capable right now"; evidence answers "did anyone
/// ever actually measure it". The badge needs both, so neither can be quietly
/// satisfied by the other.
/// </remarks>
public static class LinkSlaEvidence
{
    public const double MinAverageMilliseconds = 8;
    public const double MaxAverageMilliseconds = 10;
    public const double MaxP95Milliseconds = 20;

    /// <summary>
    /// A p95 drawn from a handful of samples is not a p95. 120 corresponds to the
    /// documented soak at two probes per second.
    /// </summary>
    public const int MinSampleCount = 120;

    /// <summary>Matches the soak length required by the measurement doc.</summary>
    public const int MinDurationSeconds = 60;

    /// <summary>
    /// True only when the run was long enough, wired, captured from the owned
    /// endpoint, and hit every published threshold.
    /// </summary>
    public static bool IsPassing(LinkMeasurementEvidence? evidence) =>
        TryExplainFailure(evidence, out _);

    /// <summary>
    /// Evaluates the evidence and, when it fails, reports the first reason in words
    /// suitable for a log or a report header.
    /// </summary>
    /// <returns>True when the evidence passes and <paramref name="reason"/> is null.</returns>
    public static bool TryExplainFailure(LinkMeasurementEvidence? evidence, out string? reason)
    {
        if (evidence is null)
        {
            reason = "no measurement has been recorded";
            return false;
        }

        if (!evidence.CaptureIsOwnedWinStreamEndpoint)
        {
            reason = "capture did not come from the WinStream virtual audio endpoint";
            return false;
        }

        if (!evidence.PathIsEthernet)
        {
            reason = "media crossed a Wi-Fi hop, which the claim excludes";
            return false;
        }

        if (!LinkSlaEligibility.IsMeasuredCaptureSlaCapable(evidence.MeasuredCaptureContributionMs))
        {
            reason = $"measured capture contribution was {evidence.MeasuredCaptureContributionMs} ms, " +
                     $"over the {LinkSlaEligibility.MaxCaptureContributionMs} ms limit";
            return false;
        }

        if (evidence.RigCalibrationMilliseconds is null)
        {
            reason = "the measurement rig was never calibrated, so the figures include its own latency";
            return false;
        }

        if (evidence.RigCalibrationMilliseconds < 0)
        {
            reason = $"rig calibration was {evidence.RigCalibrationMilliseconds:F2} ms, which is not physical";
            return false;
        }

        if (evidence.DurationSeconds < MinDurationSeconds)
        {
            reason = $"the soak ran {evidence.DurationSeconds}s, under the {MinDurationSeconds}s minimum";
            return false;
        }

        if (evidence.SampleCount < MinSampleCount)
        {
            reason = $"{evidence.SampleCount} samples cannot support a p95; {MinSampleCount} are required";
            return false;
        }

        if (evidence.Underruns > LinkSlaEligibility.MaxUnderrunsForBadge)
        {
            reason = $"{evidence.Underruns} underruns occurred; the claim requires none";
            return false;
        }

        if (evidence.AverageMilliseconds < MinAverageMilliseconds ||
            evidence.AverageMilliseconds > MaxAverageMilliseconds)
        {
            reason = $"average was {evidence.AverageMilliseconds:F2} ms, outside " +
                     $"{MinAverageMilliseconds}-{MaxAverageMilliseconds} ms";
            return false;
        }

        if (evidence.P95Milliseconds >= MaxP95Milliseconds)
        {
            reason = $"p95 was {evidence.P95Milliseconds:F2} ms, at or over the " +
                     $"{MaxP95Milliseconds} ms ceiling";
            return false;
        }

        reason = null;
        return true;
    }
}
