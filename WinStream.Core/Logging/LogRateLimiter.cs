namespace WinStream.Core.Logging;

/// <summary>
/// Lets a hot path log at most once per interval and reports how many calls were
/// swallowed in between.
/// </summary>
/// <remarks>
/// A live Extreme session wrote tens of thousands of identical lines per minute.
/// Every write takes the log file's process-wide lock, so an unlimited hot-path
/// log steals time from the capture and encode threads it is describing.
/// </remarks>
public sealed class LogRateLimiter
{
    private const long NeverLogged = long.MinValue;

    private readonly long _intervalTicks;
    private readonly TimeProvider _timeProvider;
    private long _lastLogTicks = NeverLogged;
    private long _suppressed;

    public LogRateLimiter(TimeSpan interval, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval.Ticks);
        _intervalTicks = interval.Ticks;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Calls swallowed since the last one that was allowed through.</summary>
    public long SuppressedCount => Interlocked.Read(ref _suppressed);

    /// <summary>
    /// True when the caller may log now. <paramref name="suppressedSinceLastLog"/>
    /// carries the swallowed count so the surviving line can mention it, and is
    /// reset by the same call.
    /// </summary>
    public bool ShouldLog(out long suppressedSinceLastLog)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastLogTicks);

        if (last != NeverLogged && nowTicks - last < _intervalTicks)
        {
            Interlocked.Increment(ref _suppressed);
            suppressedSinceLastLog = 0;
            return false;
        }

        // Losing the race means another thread is logging this interval already.
        if (Interlocked.CompareExchange(ref _lastLogTicks, nowTicks, last) != last)
        {
            Interlocked.Increment(ref _suppressed);
            suppressedSinceLastLog = 0;
            return false;
        }

        suppressedSinceLastLog = Interlocked.Exchange(ref _suppressed, 0);
        return true;
    }
}
