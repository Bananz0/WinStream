using WinStream.Core.Logging;

namespace WinStream.Tests;

public class LogRateLimiterTests
{
    /// <summary>Hand-wound clock so interval behavior is asserted without sleeping.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void First_call_is_always_allowed()
    {
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(5), new ManualTimeProvider());

        Assert.True(limiter.ShouldLog(out var suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void Calls_inside_the_interval_are_suppressed_and_counted()
    {
        var time = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(5), time);
        limiter.ShouldLog(out _);

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(limiter.ShouldLog(out var suppressed));
            Assert.Equal(0, suppressed);
        }

        Assert.Equal(3, limiter.SuppressedCount);
    }

    [Fact]
    public void The_surviving_line_reports_and_clears_the_suppressed_count()
    {
        var time = new ManualTimeProvider();
        var limiter = new LogRateLimiter(TimeSpan.FromSeconds(5), time);
        limiter.ShouldLog(out _);
        limiter.ShouldLog(out _);
        limiter.ShouldLog(out _);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(limiter.ShouldLog(out var suppressed));
        Assert.Equal(2, suppressed);
        Assert.Equal(0, limiter.SuppressedCount);
    }

    [Fact]
    public void Concurrent_callers_let_exactly_one_through_per_interval()
    {
        var limiter = new LogRateLimiter(TimeSpan.FromMinutes(1), new ManualTimeProvider());
        var allowed = 0;

        Parallel.For(0, 256, _ =>
        {
            if (limiter.ShouldLog(out _))
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(1, allowed);
        Assert.Equal(255, limiter.SuppressedCount);
    }

    [Fact]
    public void Non_positive_intervals_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LogRateLimiter(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LogRateLimiter(TimeSpan.FromSeconds(-1)));
    }
}
