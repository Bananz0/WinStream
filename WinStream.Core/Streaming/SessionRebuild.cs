namespace WinStream.Core.Streaming;

/// <summary>
/// The one order in which a live session may be swapped for a reconfigured one.
/// </summary>
/// <remarks>
/// Two constraints make the order load-bearing, and both fail in ways that look like
/// network faults rather than sequencing bugs:
/// <list type="bullet">
/// <item>AirPlay 2 binds PTP ports 319/320 process-wide, so the outgoing session must be
/// fully disposed before the replacement is built — overlapping them double-binds.</item>
/// <item>The receiver latches volume during SETUP, so a level applied after connect lets
/// the first seconds play at the session default.</item>
/// </list>
/// Keeping the sequence here, away from the WinUI orchestrator, makes it assertable.
/// </remarks>
public static class SessionRebuild
{
    /// <summary>
    /// Disposes <paramref name="retired"/>, builds the replacement, seeds its volume, and
    /// connects it. <paramref name="buildReplacement"/> also owns any bookkeeping that has
    /// to happen before connect, such as event wiring and registration.
    /// </summary>
    public static async Task<IAirPlaySession> ReplaceAsync(
        IAirPlaySession retired,
        Func<IAirPlaySession> buildReplacement,
        float volumeDb,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retired);
        ArgumentNullException.ThrowIfNull(buildReplacement);

        await retired.DisposeAsync().ConfigureAwait(false);

        var replacement = buildReplacement();
        await replacement.SetVolumeAsync(volumeDb, cancellationToken).ConfigureAwait(false);
        await replacement.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return replacement;
    }
}
