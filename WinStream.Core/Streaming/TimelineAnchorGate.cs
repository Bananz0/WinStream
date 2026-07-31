namespace WinStream.Core.Streaming;

/// <summary>
/// Prevents timeline publication until the RTP timebase is immutable.
/// </summary>
internal static class TimelineAnchorGate
{
    public static async Task RunAfterFreezeAsync(
        Task frozen,
        Func<CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(publish);

        await frozen.WaitAsync(cancellationToken).ConfigureAwait(false);
        await publish(cancellationToken).ConfigureAwait(false);
    }
}
