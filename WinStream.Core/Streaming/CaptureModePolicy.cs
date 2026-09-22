namespace WinStream.Core.Streaming;

/// <summary>
/// WASAPI loopback capture mode for low-latency presets.
/// Auto always uses event-driven capture; Extreme uses it when opted in via settings.
/// </summary>
public static class CaptureModePolicy
{
    /// <summary>
    /// Auto always uses event-driven loopback to minimize capture contribution at the
    /// ~50 ms floor. Extreme uses it only when the settings flag is enabled.
    /// </summary>
    public static bool WantsEventDriven(
        bool extremeEventDrivenCaptureEnabled,
        PlaybackResponsiveness responsiveness) =>
        responsiveness == PlaybackResponsiveness.Auto ||
        (extremeEventDrivenCaptureEnabled &&
         responsiveness == PlaybackResponsiveness.LabPacket);

    /// <summary>
    /// Honesty contribution for Extreme warnings: measured p95 when event-driven is
    /// warm; otherwise the frozen poll quantum.
    /// </summary>
    public static int ResolveContributionMilliseconds(
        bool useEventDrivenCapture,
        bool hasMeasuredContribution,
        int measuredContributionMilliseconds,
        int frozenPollMilliseconds = 50) =>
        useEventDrivenCapture && hasMeasuredContribution
            ? measuredContributionMilliseconds
            : frozenPollMilliseconds;

    /// <summary>
    /// Mid-ladder raises stay silent. The exhausted InfoBar may arm only at the ceiling.
    /// </summary>
    public static bool ArmsExhaustedPressureBanner(
        bool ladderExhausted,
        bool isStreaming,
        bool isSilent,
        bool pastStartupGrace) =>
        ladderExhausted &&
        isStreaming &&
        !isSilent &&
        pastStartupGrace;
}
