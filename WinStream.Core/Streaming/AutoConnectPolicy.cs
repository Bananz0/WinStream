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
        bool attemptsAvailable) =>
        enabled &&
        attemptsAvailable &&
        !connectionInFlight &&
        !string.IsNullOrWhiteSpace(lastReceiverKey) &&
        sessionState == SessionState.Disconnected;

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
