namespace WinStream.Core.Streaming;

/// <summary>
/// Extreme (~50 ms) may only attach a single receiver.
/// Kept free of WinUI so multi-room guards are unit-testable.
/// </summary>
public static class LabSessionPolicy
{
    /// <summary>
    /// Surfaced verbatim in the UI, so it uses the preset's shipped name rather than
    /// the internal "Lab" wording.
    /// </summary>
    public const string MultiRoomBlockedMessage =
        "Extreme (~50 ms) supports only one receiver. " +
        "Switch to Experimental (~250 ms) or Auto for multi-room.";

    /// <summary>
    /// Extreme asks ~48 ms of speaker lead. Capture contribution above ~40 ms leaves
    /// almost no margin for encode/Wi‑Fi — classic WASAPI loopback at 50 ms still warns.
    /// </summary>
    public const int MaxCaptureContributionMillisecondsForExtreme = 40;

    /// <summary>
    /// Shown when Extreme is selected while capture is coarser than the ~50 ms ask.
    /// Extreme remains available; this is the honesty gate, not a hard block.
    /// </summary>
    public const string CaptureTooCoarseWarning =
        "System capture is still ~50 ms loopback, so Extreme's ~50 ms speaker buffer " +
        "has almost no margin — expect it to climb toward ~80 ms or ~250 ms under load. " +
        "Continue anyway, or switch to Experimental (~250 ms)?";

    /// <summary>
    /// Shown only after Extreme's raise ladder is exhausted (live L already ~250 ms)
    /// and pressure continues. Mid-ladder raises stay silent.
    /// </summary>
    public const string RuntimePressureTitle = "Extreme is not keeping up";

    public const string RuntimePressureWarning =
        "Extreme already raised to Experimental's ~250 ms floor and still cannot keep up. " +
        "Switching to Experimental (~250 ms) restarts the stream with a clearer preset.";

    /// <summary>
    /// Returns true when an additional receiver must be refused under Extreme.
    /// </summary>
    public static bool BlocksAdditionalReceiver(
        PlaybackResponsiveness responsiveness,
        bool isFirstSession) =>
        !isFirstSession && responsiveness == PlaybackResponsiveness.LabPacket;

    /// <summary>
    /// Returns true when switching a live multi-room aggregate to Extreme must be
    /// refused. Connect blocks the second receiver, so a live preset change is the
    /// other way an Extreme session could end up fanned out.
    /// </summary>
    public static bool BlocksQualityApply(
        PlaybackResponsiveness responsiveness,
        int sessionCount) =>
        sessionCount > 1 && responsiveness == PlaybackResponsiveness.LabPacket;

    /// <summary>
    /// True when Extreme is requested but capture contribution already exceeds the
    /// near-zero-margin threshold. Does not block by itself — the UI should warn and
    /// let the user continue or fall back to Experimental.
    /// </summary>
    public static bool WarnsCaptureTooCoarse(
        PlaybackResponsiveness responsiveness,
        int captureContributionMilliseconds) =>
        responsiveness == PlaybackResponsiveness.LabPacket &&
        captureContributionMilliseconds > MaxCaptureContributionMillisecondsForExtreme;
}
