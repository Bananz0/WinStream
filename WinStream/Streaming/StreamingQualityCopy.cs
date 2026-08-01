using WinStream.Core.Streaming;

namespace WinStream.Streaming;

/// <summary>Honest UI strings for playback responsiveness / fidelity (testable without WinUI).</summary>
public static class StreamingQualityCopy
{
    public const string ExtremeLabel = "Extreme (~50 ms)";
    public const string ExtremeHint =
        "Asks ~50 ms of speaker buffer (six ALAC packets). Default capture is still " +
        "~50 ms loopback poll (optional Extreme event-driven experiment measures finer " +
        "wake spacing). Under load Extreme may climb toward ~80 ms then ~250 ms. " +
        "One speaker. Speaker buffer ask only — not a measured PC-to-speaker delay.";

    public static string LabEscapeBody =>
        "The receiver did not accept Extreme (~50 ms). " +
        "Switch to Experimental (~250 ms) and reconnect now?";

    public static string ExtremeCaptureWarningTitle => "Extreme has almost no capture margin";

    public static string ExtremeCaptureWarningBody => LabSessionPolicy.CaptureTooCoarseWarning;

    public static string StandardFidelityHint =>
        "Same conversion as Auto today (linear when the mix rate differs). Reserved for a lighter path later.";

    public static string HighFidelityHint =>
        "Lossless ALAC in every mode. Conversion matches Auto until a richer resampler ships.";

    public static string ResponsivenessInfoBody =>
        "This setting changes playback delay, not sound fidelity. Lower delay = less buffer against Wi‑Fi glitches.\n\n" +
        "• Auto — Starts near ~250 ms and may climb toward ~2 s if delivery pressure is detected.\n" +
        "• Extreme (~50 ms) — Asks ~50 ms of speaker buffer; may climb to ~80 ms then ~250 ms under " +
        "pressure (TuneBlade-style). Capture is still ~50 ms loopback. One speaker. " +
        "Speaker buffer ask only — not a measured PC-to-speaker delay.\n" +
        "• Experimental (~250 ms) — Fixed short buffer; expect stutter on some receivers.\n" +
        "• Very low (~500 ms) — Fixed half-second buffer.\n" +
        "• Low delay (~1 s) — Snappier than Balanced; more stutter risk.\n" +
        "• Balanced (~1.5 s) — Fixed mid buffer.\n" +
        "• Most stable (~2 s) — Apple’s standard realtime buffer.\n\n" +
        "Changing a preset while streaming reconnects so it takes effect immediately.\n\n" +
        "WinStream does not guarantee a specific millisecond AirPlay delay.";
}
