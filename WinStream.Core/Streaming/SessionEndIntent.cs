namespace WinStream.Core.Streaming;

/// <summary>
/// Decides whether an aggregate terminal transition was caused by the user emptying
/// the session map. A partial disconnect that leaves rooms streaming must not suppress
/// a later lost-session auto-connect re-arm.
/// </summary>
public static class SessionEndIntent
{
    /// <summary>
    /// True when a user <c>Disconnect</c> API left no sessions — the same suppress
    /// signal as Disconnect-all. False when sessions remain or the end was not user-driven.
    /// </summary>
    public static bool UserRequested(bool userDisconnectApi, bool sessionsRemain) =>
        userDisconnectApi && !sessionsRemain;
}
