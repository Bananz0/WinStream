namespace WinStream.Core.Streaming;

/// <summary>
/// Pure Extreme lab rules for the optional event-driven WASAPI loopback experiment.
/// Keeps flag∧LabPacket gating and contribution resolution unit-testable without WinUI.
/// </summary>
public static class ExtremeCaptureExperiment
{
    /// <summary>
    /// Event-driven loopback is Extreme-only and opt-in. Other presets stay on the
    /// frozen 50 ms poll even when the settings flag is true.
    /// </summary>
    public static bool WantsEventDriven(
        bool extremeEventDrivenCaptureEnabled,
        PlaybackResponsiveness responsiveness) =>
        extremeEventDrivenCaptureEnabled &&
        responsiveness == PlaybackResponsiveness.LabPacket;

    /// <summary>
    /// Honesty contribution for Extreme warnings: measured p95 when the experiment is
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
        PlaybackResponsiveness responsiveness,
        bool ladderExhausted,
        bool isStreaming,
        bool isSilent,
        bool pastStartupGrace) =>
        responsiveness == PlaybackResponsiveness.LabPacket &&
        ladderExhausted &&
        isStreaming &&
        !isSilent &&
        pastStartupGrace;
}
