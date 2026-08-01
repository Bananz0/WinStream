using WinStream.Core.Network;

namespace WinStream.Core.Streaming;

/// <summary>
/// Decides whether a discovery pass should trigger an automatic connection to the
/// remembered receiver. Kept free of UI state so the gates are testable.
/// </summary>
public static class AutoConnectPolicy
{
    public static bool ShouldAttempt(
        bool enabled,
        string? lastReceiverKey,
        SessionState sessionState,
        bool connectionInFlight,
        bool attemptsAvailable,
        PlaybackResponsiveness responsiveness = PlaybackResponsiveness.Auto) =>
        enabled &&
        attemptsAvailable &&
        !connectionInFlight &&
        !string.IsNullOrWhiteSpace(lastReceiverKey) &&
        sessionState == SessionState.Disconnected &&
        responsiveness != PlaybackResponsiveness.LabPacket;

    /// <summary>
    /// True when a session ended on its own and auto-connect should become eligible
    /// again. A disconnect the user asked for must stay disconnected, and a connect
    /// that never established (failed dial, refused pairing) is left to the retry
    /// budget rather than re-armed here.
    /// </summary>
    public static bool ReArmsAfterSessionEnd(
        SessionState previous,
        SessionState current,
        bool userInitiated) =>
        !userInitiated &&
        current is SessionState.Disconnected or SessionState.Failed &&
        previous is SessionState.Streaming or SessionState.Degraded or SessionState.Reconnecting;

    public static DeviceInfo? FindTarget(
        IEnumerable<DeviceInfo> discovered,
        string? lastReceiverKey)
    {
        if (discovered is null || string.IsNullOrWhiteSpace(lastReceiverKey))
        {
            return null;
        }

        return discovered.FirstOrDefault(device => ReceiverKey.Matches(lastReceiverKey, device));
    }
}
