using WinStream.Core;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class SinkModeCoordinatorTests
{
    [Fact]
    public void Requires_both_stop_actions()
    {
        Assert.Throws<ArgumentNullException>(() => new SinkModeCoordinator(null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new SinkModeCoordinator(_ => Task.CompletedTask, null!));
    }

    [Fact]
    public async Task Switching_away_from_AirPlay_stops_AirPlay()
    {
        var log = new StopLog();

        var tornDown = await log.Coordinator.PrepareSwitchAsync(SinkMode.AirPlay, SinkMode.Link);

        Assert.True(tornDown);
        Assert.Equal(1, log.AirPlayStops);
        Assert.Equal(0, log.LinkStops);
    }

    [Fact]
    public async Task Switching_away_from_Link_stops_Link()
    {
        var log = new StopLog();

        var tornDown = await log.Coordinator.PrepareSwitchAsync(SinkMode.Link, SinkMode.AirPlay);

        Assert.True(tornDown);
        Assert.Equal(0, log.AirPlayStops);
        Assert.Equal(1, log.LinkStops);
    }

    [Theory]
    [InlineData(SinkMode.AirPlay)]
    [InlineData(SinkMode.Link)]
    public async Task A_no_op_switch_tears_nothing_down(SinkMode mode)
    {
        var log = new StopLog();

        var tornDown = await log.Coordinator.PrepareSwitchAsync(mode, mode);

        Assert.False(tornDown);
        Assert.Equal(0, log.AirPlayStops);
        Assert.Equal(0, log.LinkStops);
    }

    [Fact]
    public async Task Connecting_Link_stops_AirPlay_first()
    {
        var log = new StopLog();

        await log.Coordinator.EnsureExclusiveAsync(SinkMode.Link);

        Assert.Equal(1, log.AirPlayStops);
        Assert.Equal(0, log.LinkStops);
    }

    [Fact]
    public async Task Connecting_AirPlay_stops_Link_first()
    {
        var log = new StopLog();

        await log.Coordinator.EnsureExclusiveAsync(SinkMode.AirPlay);

        Assert.Equal(0, log.AirPlayStops);
        Assert.Equal(1, log.LinkStops);
    }

    [Fact]
    public async Task A_failing_teardown_surfaces_instead_of_letting_both_sinks_run()
    {
        var coordinator = new SinkModeCoordinator(
            _ => throw new InvalidOperationException("boom"),
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PrepareSwitchAsync(SinkMode.AirPlay, SinkMode.Link));
    }

    [Fact]
    public async Task Passes_the_cancellation_token_to_the_stop_action()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        var coordinator = new SinkModeCoordinator(
            ct =>
            {
                seen = ct;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask);

        await coordinator.StopAsync(SinkMode.AirPlay, cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    private sealed class StopLog
    {
        public StopLog()
        {
            Coordinator = new SinkModeCoordinator(
                _ =>
                {
                    AirPlayStops++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    LinkStops++;
                    return Task.CompletedTask;
                });
        }

        public SinkModeCoordinator Coordinator { get; }

        public int AirPlayStops { get; private set; }

        public int LinkStops { get; private set; }
    }
}
