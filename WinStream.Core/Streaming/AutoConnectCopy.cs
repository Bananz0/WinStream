namespace WinStream.Core.Streaming;

/// <summary>
/// Honest UI strings for auto-connect: the toggle must name the preferred receiver,
/// and the per-row star must say what it does without sounding like Connect.
/// </summary>
public static class AutoConnectCopy
{
    public const string ToggleTitle = "Auto-connect";

    public const string PreferToolTip = "Use for auto-connect";

    public const string PreferredToolTip = "Preferred for auto-connect";

    public const string PreferAutomationName = "Use this device for auto-connect";

    public const string PreferredAutomationName = "Preferred device for auto-connect";

    public const string PreferredBadge = "Preferred";

    /// <summary>No preferred receiver yet — tell the user how to pick one.</summary>
    public const string NoPreferredDescription =
        "Pick a preferred device with the star, or connect once to set it.";

    public static string OnDescription(string receiverName) =>
        $"Auto-connects to {receiverName} when it appears.";

    public static string OffDescription(string receiverName) =>
        $"Preferred: {receiverName}. Turn on to reconnect automatically.";

    /// <summary>
    /// The remembered receiver has not shown up in discovery yet. Worded distinctly
    /// from <see cref="WaitingOnMacRetryingStatus"/> even though both retry on the
    /// same unbounded backoff — a genuinely absent receiver and a SETUP-400 after a
    /// successful pair-verify are different situations for the user, even if the
    /// retry cadence can't yet tell them apart.
    /// </summary>
    public const string ReceiverNotFoundRetryingStatus = "Receiver not found (retrying)";

    /// <summary>
    /// The SETUP-400 pattern: a persisted pair-verify succeeded but SETUP failed,
    /// which field logs show is at least as consistent with a digest/pairing
    /// protocol bug as an unanswered Mac Accept prompt. Classified
    /// <see cref="AutoConnectFailureKind.Silent"/> like a not-found receiver, with
    /// the same unbounded retry — only this status text differs.
    /// </summary>
    public const string WaitingOnMacRetryingStatus = "Waiting on the Mac (retrying)";

    /// <summary>
    /// A Config-kind (401/403/470) rejection: an explicit no from the receiver that
    /// cannot succeed without the user acting, so it is not presented as an endless
    /// retry the way the Silent statuses above are.
    /// </summary>
    public const string PasswordRejectedStatus = "Password rejected";

    /// <summary>
    /// The status row for an automatic-reconnect attempt that failed, honest about
    /// whether it will keep retrying (<see cref="AutoConnectFailureKind.Silent"/> /
    /// <see cref="AutoConnectFailureKind.Other"/>) or has stopped until the user
    /// acts (<see cref="AutoConnectFailureKind.Config"/>).
    /// </summary>
    public static string RetryingStatus(AutoConnectFailureKind kind, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return kind switch
        {
            AutoConnectFailureKind.Config => PasswordRejectedStatus,
            _ when AutoConnectFailureKindClassifier.IsSetupBadRequest(failure) => WaitingOnMacRetryingStatus,
            _ => ReceiverNotFoundRetryingStatus
        };
    }
}
