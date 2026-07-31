using WinStream.Core;

namespace WinStream.Streaming;

/// <summary>Honest UI strings for playback responsiveness / fidelity (testable without WinUI).</summary>
public static class StreamingQualityCopy
{
    public const string ExtremeLabel = "Extreme (~8 ms)";
    public const string ExtremeHint =
        "Shortest delay WinStream can request. Often stutters or fails — for testing only, one speaker.";

    public static string LabEscapeBody =>
        "The receiver did not accept Extreme (~8 ms). " +
        "Switch to Experimental (~250 ms) and reconnect now?";

    /// <summary>
    /// Shown when a receiver asks for the persistent-pairing secret. Covers both forms
    /// the same SRP exchange accepts: the on-screen code, or the AirPlay Receiver
    /// password when one is configured.
    /// </summary>
    public static string PairingPromptBody =>
        "Look at the Mac for a 4-digit AirPlay code and type it here. " +
        "That trusts this PC so later connects can skip Accept.\n\n" +
        "If you set a password under System Settings → General → AirDrop & Handoff → " +
        "AirPlay Receiver, enter that password instead.\n\n" +
        "If nothing appears, click Skip — you'll keep getting the Accept prompt.";

    public static string AutoConnectOnDescription(string receiverName) =>
        $"On launch (or when you turn this on), connect once to {receiverName} when it appears.";

    public static string StandardFidelityHint =>
        "Same conversion as Auto today (linear when the mix rate differs). Reserved for a lighter path later.";

    public static string HighFidelityHint =>
        "Lossless ALAC in every mode. Conversion matches Auto until a richer resampler ships.";

    public static string ResponsivenessInfoBody =>
        "This setting changes playback delay, not sound fidelity. Lower delay = less buffer against Wi‑Fi glitches.\n\n" +
        "• Auto — Starts near ~250 ms and may climb toward ~2 s if delivery pressure is detected.\n" +
        "• Extreme (~8 ms) — Testing only; often fails. One speaker.\n" +
        "• Experimental (~250 ms) — Fixed short buffer; expect stutter on some receivers.\n" +
        "• Very low (~500 ms) — Fixed half-second buffer.\n" +
        "• Low delay (~1 s) — Snappier than Balanced; more stutter risk.\n" +
        "• Balanced (~1.5 s) — Fixed mid buffer.\n" +
        "• Most stable (~2 s) — Apple’s standard realtime buffer.\n\n" +
        "Changing a preset while streaming reconnects so it takes effect immediately.\n\n" +
        "WinStream does not guarantee a specific millisecond AirPlay delay.";
}
