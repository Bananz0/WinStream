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
}
