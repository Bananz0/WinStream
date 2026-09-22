namespace WinStream.Core.Streaming.Link;

/// <summary>What the UI is allowed to claim about the running Link capture path.</summary>
public enum LinkCaptureQuality
{
    NotStreaming,

    /// <summary>Shared-mode loopback: works, but can never back an 8–10 ms claim.</summary>
    LegacyLoopback,

    /// <summary>Owned VAD, not enough callbacks observed yet.</summary>
    VadMeasuring,

    /// <summary>Owned VAD with measured callbacks inside the 3 ms budget.</summary>
    VadWithinBudget,

    /// <summary>Owned VAD, but measured callbacks exceed the 3 ms budget.</summary>
    VadOverBudget
}

/// <summary>Pure mapping from capture facts to the claim the UI may render.</summary>
public static class LinkCaptureQualityPolicy
{
    public static LinkCaptureQuality Evaluate(
        bool isStreaming,
        bool isOwnedWinStreamEndpoint,
        int measuredContributionMilliseconds)
    {
        if (!isStreaming)
        {
            return LinkCaptureQuality.NotStreaming;
        }

        if (!isOwnedWinStreamEndpoint)
        {
            return LinkCaptureQuality.LegacyLoopback;
        }

        if (measuredContributionMilliseconds <= 0)
        {
            return LinkCaptureQuality.VadMeasuring;
        }

        return LinkSlaEligibility.IsMeasuredCaptureSlaCapable(measuredContributionMilliseconds)
            ? LinkCaptureQuality.VadWithinBudget
            : LinkCaptureQuality.VadOverBudget;
    }
}
