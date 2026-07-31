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

    [Fact]
    public void Start_activates_the_budget_and_reports_remaining_time()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
        var budget = new ReconnectBudget(TimeSpan.FromSeconds(30), time);

        Assert.False(budget.IsActive);
        Assert.Null(budget.Remaining);

        budget.Start();

        Assert.True(budget.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(30), budget.Remaining);
    }

    [Fact]
    public void Clear_deactivates_an_expired_budget()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
        var budget = new ReconnectBudget(TimeSpan.FromSeconds(30), time);

        budget.Start();
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(budget.IsExpired);

        budget.Clear();

        Assert.False(budget.IsActive);
        Assert.False(budget.IsExpired);
        Assert.Null(budget.Remaining);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
