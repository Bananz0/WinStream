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
}
