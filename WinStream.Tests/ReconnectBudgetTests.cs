using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class ReconnectBudgetTests
{
    [Fact]
    public void Expires_after_configured_budget()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
        var budget = new ReconnectBudget(TimeSpan.FromSeconds(30), time);

        budget.Start();
        Assert.False(budget.IsExpired);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(budget.IsExpired);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(budget.IsExpired);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
