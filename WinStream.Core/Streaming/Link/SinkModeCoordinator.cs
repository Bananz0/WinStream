namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Single owner of the AirPlay/Link exclusivity rule.
/// </summary>
/// <remarks>
/// Both sinks drive the same capture endpoint, so the outgoing one has to be fully torn
/// down before the incoming one runs. That rule used to be spelled out in each UI event
/// handler, where one missed teardown quietly reintroduces concurrent sinks. Routing
/// every transition through here keeps the invariant in one testable place; the caller
/// supplies the two stop actions so this stays free of WinUI and socket types.
/// </remarks>
public sealed class SinkModeCoordinator
{
    private readonly Func<CancellationToken, Task> _stopAirPlay;
    private readonly Func<CancellationToken, Task> _stopLink;

    public SinkModeCoordinator(
        Func<CancellationToken, Task> stopAirPlay,
        Func<CancellationToken, Task> stopLink)
    {
        ArgumentNullException.ThrowIfNull(stopAirPlay);
        ArgumentNullException.ThrowIfNull(stopLink);
        _stopAirPlay = stopAirPlay;
        _stopLink = stopLink;
    }

    /// <summary>
    /// Tears down the outgoing sink when the mode actually changes. Returns true when a
    /// teardown ran, so the caller can tell the user the stream was interrupted.
    /// </summary>
    public async Task<bool> PrepareSwitchAsync(
        SinkMode previous,
        SinkMode next,
        CancellationToken cancellationToken = default)
    {
        if (!SinkModeSwitchPolicy.RequiresTeardown(previous, next))
        {
            return false;
        }

        await StopAsync(previous, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Stops the sink that is not <paramref name="mode"/>, before it connects.</summary>
    public Task EnsureExclusiveAsync(SinkMode mode, CancellationToken cancellationToken = default) =>
        StopAsync(mode == SinkMode.AirPlay ? SinkMode.Link : SinkMode.AirPlay, cancellationToken);

    public Task StopAsync(SinkMode mode, CancellationToken cancellationToken = default) =>
        mode == SinkMode.AirPlay ? _stopAirPlay(cancellationToken) : _stopLink(cancellationToken);
}
