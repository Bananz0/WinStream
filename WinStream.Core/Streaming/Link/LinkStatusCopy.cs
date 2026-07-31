namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Severity for the Link status line. Maps to theme brushes in the window;
/// kept as an enum so Core stays free of UI types.
/// </summary>
public enum LinkUiTone
{
    Neutral,
    Progress,
    Caution,
    Success,
    Critical
}

/// <summary>
/// One user-facing Link status. The window renders <see cref="Headline"/> in the
/// status line and <see cref="Pill"/> in the session pill; <see cref="Detail"/> is
/// optional secondary copy. <see cref="ClaimsSla"/> is the only place that may
/// authorize the words "8–10 ms".
/// </summary>
public sealed record LinkUiMessage(
    string Headline,
    string? Detail,
    string Pill,
    LinkUiTone Tone,
    bool ClaimsSla);

/// <summary>
/// Facts the Link status copy needs. Kept as a record so the window can pass
/// whatever it knows today and leave unknowns as false — false is the honest
/// default for every claim gate.
/// </summary>
public sealed record LinkUiContext(
    LinkSessionState Session,
    LinkCaptureQuality CaptureQuality,
    int MeasuredCaptureMilliseconds,
    bool PathIsEthernet,
    long UnderrunCount,
    bool MeasurementEvidencePasses,
    LinkConnectStatus? LastFailure = null,
    string? FailureDetail = null,
    bool IsScanning = false,
    bool IsConnecting = false);

/// <summary>
/// Pure UI copy for WinStream Link. Every string the window shows for Link
/// status should come from here so "8–10 ms" cannot leak in through ad-hoc
/// formatting in MainWindow.
/// </summary>
public static class LinkStatusCopy
{
    public const string SlaPhrase = "8–10 ms";

    public const string CardTitle = "WinStream Link";

    /// <summary>
    /// Card subtitle. Deliberately does not say "ultra-low" or quote a number —
    /// those only appear after a measured, eligible session.
    /// </summary>
    public const string CardHint =
        "Companion path for the lowest latency WinStream can measure. " +
        "Requires a LinkRx companion outside this app. " +
        "AirPlay and Link cannot run at the same time.";

    public const string ConnectButton = "Connect Link";

    public const string DisconnectButton = "Disconnect Link";

    public static LinkUiMessage For(LinkUiContext context)
    {
        if (context.IsConnecting)
        {
            return new LinkUiMessage(
                "Connecting to companion…",
                "Opening capture and the control channel. The latency claim stays off until the path is checked.",
                "Connecting…",
                LinkUiTone.Progress,
                ClaimsSla: false);
        }

        if (context.IsScanning)
        {
            return new LinkUiMessage(
                "Scanning for companions…",
                "Companions advertise themselves on the local network. You can still type an IP.",
                "Scanning…",
                LinkUiTone.Progress,
                ClaimsSla: false);
        }

        if (context.LastFailure is { } failure &&
            context.Session is not LinkSessionState.Streaming)
        {
            return ForFailure(failure, context.FailureDetail);
        }

        return context.Session switch
        {
            LinkSessionState.Streaming => ForStreaming(context),
            LinkSessionState.Failed => new LinkUiMessage(
                "Link failed.",
                "Disconnect and try again, or check the companion is still running.",
                "Link failed",
                LinkUiTone.Critical,
                ClaimsSla: false),
            _ => Idle()
        };
    }

    /// <summary>Idle / disconnected — streaming is available; the claim is not implied.</summary>
    public static LinkUiMessage Idle() =>
        new(
            "Link idle.",
            "Connect a companion to stream. The measured " + SlaPhrase +
            " claim needs Ethernet, the WinStream driver, and a passing lab soak — " +
            "streaming works without that.",
            "Link idle",
            LinkUiTone.Neutral,
            ClaimsSla: false);

    public static LinkUiMessage ForFailure(LinkConnectStatus status, string? detail) =>
        status switch
        {
            LinkConnectStatus.MissingPin => new LinkUiMessage(
                "Enter the PIN shown by the companion.",
                "The companion prints a PIN when it starts. Saved PINs are filled in automatically after the first success.",
                "PIN needed",
                LinkUiTone.Caution,
                ClaimsSla: false),
            LinkConnectStatus.InvalidTarget => new LinkUiMessage(
                "Enter the companion as an IP or IP:port.",
                "Example: 192.168.1.50 or [fe80::1]:47200.",
                "Address needed",
                LinkUiTone.Caution,
                ClaimsSla: false),
            LinkConnectStatus.PinRejected => new LinkUiMessage(
                "That PIN was rejected.",
                "Check the PIN printed by the companion and try again.",
                "PIN rejected",
                LinkUiTone.Critical,
                ClaimsSla: false),
            LinkConnectStatus.CaptureFailed => new LinkUiMessage(
                "Could not start capture.",
                string.IsNullOrWhiteSpace(detail)
                    ? "Check that a playback device is available, then try again."
                    : detail,
                "Capture failed",
                LinkUiTone.Critical,
                ClaimsSla: false),
            LinkConnectStatus.TransportFailed => new LinkUiMessage(
                "Could not reach the companion.",
                string.IsNullOrWhiteSpace(detail)
                    ? "Confirm the IP, that LinkRx is running, and that both machines are on the same network."
                    : detail,
                "Unreachable",
                LinkUiTone.Critical,
                ClaimsSla: false),
            _ => new LinkUiMessage(
                "Link could not connect.",
                detail,
                "Link failed",
                LinkUiTone.Critical,
                ClaimsSla: false)
        };

    public static LinkUiMessage ScanResult(int companionCount) =>
        companionCount <= 0
            ? new LinkUiMessage(
                "No companions found.",
                "Start LinkRx on the other machine, or enter its IP manually.",
                "No companions",
                LinkUiTone.Caution,
                ClaimsSla: false)
            : new LinkUiMessage(
                companionCount == 1
                    ? "Found 1 companion."
                    : $"Found {companionCount} companions.",
                "Select one from the list, or keep typing an IP.",
                "Companions found",
                LinkUiTone.Success,
                ClaimsSla: false);

    public static LinkUiMessage ScanFailed(string? detail) =>
        new(
            "Scan failed.",
            string.IsNullOrWhiteSpace(detail)
                ? "You can still connect by typing the companion IP."
                : detail,
            "Scan failed",
            LinkUiTone.Critical,
            ClaimsSla: false);

    public static LinkUiMessage Disconnected() => Idle();

    private static LinkUiMessage ForStreaming(LinkUiContext context)
    {
        var captureMs = context.MeasuredCaptureMilliseconds;
        var ownedWithinBudget = context.CaptureQuality == LinkCaptureQuality.VadWithinBudget;
        var sessionEligible = LinkSlaEligibility.IsEligible(
            captureMs,
            captureIsOwnedWinStreamEndpoint: ownedWithinBudget,
            pathIsEthernet: context.PathIsEthernet,
            underrunCount: context.UnderrunCount);

        // Proven claim: session eligibility and a recorded measurement must both pass.
        // Neither gate alone is enough to put "8–10 ms" on screen.
        if (sessionEligible && context.MeasurementEvidencePasses)
        {
            return new LinkUiMessage(
                $"Link connected · Measured {SlaPhrase}",
                "Lab evidence on file for this path: average in band, p95 under 20 ms, " +
                "no underruns, Ethernet, calibrated rig. Live path still meets eligibility. " +
                "This is a measured lab claim — not a guarantee on every network.",
                $"Measured {SlaPhrase}",
                LinkUiTone.Success,
                ClaimsSla: true);
        }

        return context.CaptureQuality switch
        {
            LinkCaptureQuality.LegacyLoopback => new LinkUiMessage(
                "Link connected · Standard capture",
                "Audio is streaming via system loopback. That path is reliable but too slow " +
                "for the lab claim. Install and select the WinStream audio driver for the " +
                "low-latency path.",
                "Link streaming",
                LinkUiTone.Success,
                ClaimsSla: false),
            LinkCaptureQuality.VadMeasuring => new LinkUiMessage(
                "Link connected · Measuring capture",
                "WinStream driver is active. Capture timing is being measured; the lab claim " +
                "is not shown until timing and path checks finish.",
                "Measuring…",
                LinkUiTone.Progress,
                ClaimsSla: false),
            LinkCaptureQuality.VadOverBudget => new LinkUiMessage(
                "Link connected · Capture over budget",
                $"Driver capture timing is {captureMs} ms, above the " +
                $"{LinkSlaEligibility.MaxCaptureContributionMs} ms lab budget, so the claim " +
                "stays off. Streaming continues with higher delay.",
                "Link streaming",
                LinkUiTone.Caution,
                ClaimsSla: false),
            LinkCaptureQuality.VadWithinBudget when !context.PathIsEthernet => new LinkUiMessage(
                "Link connected · Wi‑Fi path",
                "Streaming on wireless. The published " + SlaPhrase +
                " claim is Ethernet-lab only, so the badge stays off. " +
                "Expect higher delay; audio should still play.",
                "Link streaming",
                LinkUiTone.Success,
                ClaimsSla: false),
            LinkCaptureQuality.VadWithinBudget when context.UnderrunCount >
                LinkSlaEligibility.MaxUnderrunsForBadge => new LinkUiMessage(
                "Link connected · Underruns reported",
                $"Receiver reported {context.UnderrunCount} underrun(s). " +
                "Audio may have gaps; the measured claim stays off until a clean run.",
                "Link streaming",
                LinkUiTone.Caution,
                ClaimsSla: false),
            LinkCaptureQuality.VadWithinBudget => new LinkUiMessage(
                "Link connected · Low-latency path · Proof pending",
                "This session uses the WinStream driver over Ethernet with clean capture " +
                "timing. The " + SlaPhrase + " badge appears only after a recorded lab soak.",
                "Proof pending",
                LinkUiTone.Success,
                ClaimsSla: false),
            _ => new LinkUiMessage(
                "Link connected",
                "Audio is streaming.",
                "Link streaming",
                LinkUiTone.Success,
                ClaimsSla: false)
        };
    }
}
