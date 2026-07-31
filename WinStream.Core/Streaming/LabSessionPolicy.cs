namespace WinStream.Core.Streaming;

/// <summary>
/// Lab (one-packet) latency may only attach a single receiver.
/// Kept free of WinUI so multi-room guards are unit-testable.
/// </summary>
public static class LabSessionPolicy
{
    /// <summary>
    /// Surfaced verbatim in the UI, so it uses the preset's shipped name rather than
    /// the internal "Lab" wording.
    /// </summary>
    public const string MultiRoomBlockedMessage =
        "Extreme (~8 ms) supports only one receiver. " +
        "Switch to Experimental (~250 ms) or Auto for multi-room.";

    /// <summary>
    /// One ALAC packet is ~8 ms. Capture contribution above ~2× that period cannot
    /// feed Extreme without burst/starve — classic WASAPI loopback at 50 ms is already over.
    /// </summary>
    public const int MaxCaptureContributionMillisecondsForExtreme = 16;

    /// <summary>
    /// Shown when Extreme is selected while capture is coarser than the packet period.
    /// Extreme remains available as a wire probe; this is the honesty gate, not a hard block.
    /// </summary>
    public const string CaptureTooCoarseWarning =
        "System capture alone is coarser than Extreme's ~8 ms packet. " +
        "Extreme only shrinks the speaker buffer — expect stutter. " +
        "Continue anyway, or switch to Experimental (~250 ms)?";

    /// <summary>
    /// Returns true when an additional receiver must be refused under Lab.
    /// </summary>
    public static bool BlocksAdditionalReceiver(
        PlaybackResponsiveness responsiveness,
        bool isFirstSession) =>
        !isFirstSession && responsiveness == PlaybackResponsiveness.LabPacket;

    /// <summary>
    /// Returns true when switching a live multi-room aggregate to Lab must be
    /// refused. Connect blocks the second receiver, so a live preset change is the
    /// other way a Lab session could end up fanned out.
    /// </summary>
    public static bool BlocksQualityApply(
        PlaybackResponsiveness responsiveness,
        int sessionCount) =>
        sessionCount > 1 && responsiveness == PlaybackResponsiveness.LabPacket;

    /// <summary>
    /// True when Extreme is requested but capture contribution already exceeds ~2× the
    /// packet period. Does not block by itself — the UI should warn and let the user
    /// continue as a probe or fall back to Experimental.
    /// </summary>
    public static bool WarnsCaptureTooCoarse(
        PlaybackResponsiveness responsiveness,
        int captureContributionMilliseconds) =>
        responsiveness == PlaybackResponsiveness.LabPacket &&
        captureContributionMilliseconds > MaxCaptureContributionMillisecondsForExtreme;
}
