namespace WinStream.Core.Streaming;

/// <summary>
/// Freezes the RTP timebase on the first submitted chunk, adopting a fan-out
/// clock stamp when one is supplied. Restamping every submit sawtooths against
/// ALAC packet boundaries and makes receivers mark packets late; rebasing after
/// the timeline anchor has been published strands the receiver on a timebase the
/// stream no longer uses.
/// </summary>
internal static class SharedMediaClockAlignment
{
    /// <summary>
    /// Returns true when this call froze the timebase, meaning the caller may
    /// now publish an anchor describing it.
    /// </summary>
    public static bool Freeze(
        ref uint rtpTimestamp,
        ref bool basePending,
        uint? sharedMediaTimestamp)
    {
        if (!basePending)
        {
            return false;
        }

        if (sharedMediaTimestamp.HasValue)
        {
            rtpTimestamp = sharedMediaTimestamp.Value;
        }

        basePending = false;
        return true;
    }
}
