namespace WinStream.Core.Streaming;

/// <summary>
/// Honest UI strings for AirPlay pairing (testable without WinUI). Pairing copy has
/// to stay blunt about the cost of skipping: a temporary pairing means the receiver
/// asks the user to approve every single session.
/// </summary>
public static class PairingCopy
{
    /// <summary>
    /// Shown when a receiver asks for the on-screen AirPlay code during persistent
    /// pairing. Password-protected receivers use <see cref="PasswordPromptBody"/> instead.
    /// </summary>
    public const string PromptBody =
        "Look at the Mac for a 4-digit AirPlay code and type it here. " +
        "That trusts this PC so later connects can skip Accept.\n\n" +
        "If nothing appears, click Skip (approve every time) — the receiver will keep " +
        "asking you to approve each session.";

    public const string TrustButton = "Trust this PC";

    public const string SkipButton = "Skip (approve every time)";

    public const string PasswordPromptTitle = "Enter AirPlay password";

    /// <summary>
    /// Shown when mDNS advertises PasswordRequired. This is the System Settings
    /// AirPlay Receiver password, not the on-screen code.
    /// </summary>
    public const string PasswordPromptBody =
        "This Mac has an AirPlay password under System Settings → General → " +
        "AirDrop & Handoff → AirPlay Receiver. Enter that password to connect.\n\n" +
        "WinStream stores it encrypted on this PC so you are not asked every time.";

    public const string PasswordButton = "Connect";

    public const string PasswordCancelButton = "Cancel";

    public const string TransientTitle = "Approve needed on every connect";

    public const string TransientBody =
        "WinStream connected with temporary pairing, so the receiver asks you to " +
        "approve each session. To trust this PC once, disconnect and connect again, " +
        "then type the AirPlay code the receiver shows instead of skipping it.";

    public const string TransientStatus = "Temporary pairing";

    public const string ForgetButton = "Forget pairing";

    public const string ForgetDoneTitle = "Pairing cleared";

    public const string ForgetDoneBody =
        "Saved trust for this receiver is gone. The next connect will ask for the " +
        "AirPlay code or password again.";

    public const string ForgetNothingBody =
        "No saved pairing for this receiver. Connect again when you want to trust it.";
}
