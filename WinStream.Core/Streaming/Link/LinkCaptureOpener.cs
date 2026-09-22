namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Buffer-size ladder for opening Link capture: ask for the SLA budget first, then
/// accept the 10 ms fallback so a rejecting driver degrades instead of failing.
/// </summary>
public static class LinkCaptureOpener
{
    public static readonly IReadOnlyList<int> DefaultAttemptsMilliseconds = new[]
    {
        LinkSlaEligibility.MaxCaptureContributionMs,
        LinkSlaEligibility.FallbackCaptureBufferMs
    };

    /// <summary>
    /// Calls <paramref name="open"/> for each buffer size until one succeeds.
    /// Throws the last error when every attempt fails.
    /// </summary>
    public static LinkCaptureOpenResult<T> Open<T>(
        Func<int, T> open,
        IReadOnlyList<int>? attemptsMilliseconds = null,
        Action<int, Exception>? onAttemptFailed = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        var attempts = attemptsMilliseconds ?? DefaultAttemptsMilliseconds;
        if (attempts.Count == 0)
        {
            throw new ArgumentException("At least one buffer attempt is required.", nameof(attemptsMilliseconds));
        }

        Exception? lastError = null;
        foreach (var bufferMs in attempts)
        {
            try
            {
                return new LinkCaptureOpenResult<T>(
                    open(bufferMs),
                    bufferMs,
                    IsFallback: bufferMs != attempts[0]);
            }
            catch (Exception ex)
            {
                lastError = ex;
                onAttemptFailed?.Invoke(bufferMs, ex);
            }
        }

        throw lastError ?? new InvalidOperationException("Link capture failed to open.");
    }
}

/// <summary>Whichever attempt the driver accepted.</summary>
public readonly record struct LinkCaptureOpenResult<T>(
    T Capture,
    int AcceptedBufferMilliseconds,
    bool IsFallback);
