namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Pure policy for when UI may show the Ethernet 8–10 ms SLA badge.
/// </summary>
public static class LinkSlaEligibility
{
    public const int MaxCaptureContributionMs = 3;
    public const int FallbackCaptureBufferMs = 10;
    public const int MaxUnderrunsForBadge = 0;

    /// <summary>
    /// True only when capture is short enough, path is Ethernet, and underruns are zero.
    /// </summary>
    public static bool IsEligible(
        int captureContributionMs,
        bool captureIsOwnedWinStreamEndpoint,
        bool pathIsEthernet,
        long underrunCount)
    {
        if (!captureIsOwnedWinStreamEndpoint || !pathIsEthernet)
        {
            return false;
        }

        if (captureContributionMs <= 0 || captureContributionMs > MaxCaptureContributionMs)
        {
            return false;
        }

        return underrunCount <= MaxUnderrunsForBadge;
    }

    /// <summary>
    /// Measured capture callback contribution must be positive and no greater than 3 ms.
    /// Requested client-buffer sizes are not valid input to this policy.
    /// </summary>
    public static bool IsMeasuredCaptureSlaCapable(int measuredContributionMilliseconds) =>
        measuredContributionMilliseconds > 0 &&
        measuredContributionMilliseconds <= MaxCaptureContributionMs;
}
