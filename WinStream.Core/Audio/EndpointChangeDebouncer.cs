namespace WinStream.Core.Audio;

/// <summary>
/// Pure debounce decision for a WASAPI default-render-endpoint-changed notification.
/// </summary>
/// <remarks>
/// Windows' <c>IMMNotificationClient.OnDefaultDeviceChanged</c> fires once per role
/// (<c>eConsole</c>, <c>eMultimedia</c>, <c>eCommunications</c>) for a single physical
/// default-device switch, so a naive "rebind on every callback" handler tears down and
/// rebuilds capture two or three times in a row for one user action. The callback also
/// arrives on a non-UI COM thread, so the decision itself must not touch capture state —
/// it only answers "has enough time passed since the last rebind to treat this as a new
/// change." <c>WasapiLoopbackSource</c> (WinUI project, outside this assembly) owns the
/// actual <c>RebindAsync</c> call and marshaling; this stays a pure, <see cref="TimeProvider"/>-
/// driven function so it can be unit tested directly without a WinUI-only test host.
/// </remarks>
public static class EndpointChangeDebouncer
{
    /// <summary>
    /// Minimum spacing between two rebinds triggered by default-endpoint-changed
    /// notifications. Comfortably wider than the sub-100ms spacing observed between
    /// per-role notifications for a single physical device change, while still short
    /// enough that a genuine second device switch moments later is not swallowed.
    /// </summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// True when <paramref name="now"/> is far enough past <paramref name="lastRebind"/>
    /// that a default-endpoint-changed notification arriving now should trigger another
    /// rebind, rather than being folded into the one already just performed.
    /// </summary>
    public static bool ShouldRebind(DateTimeOffset lastRebind, DateTimeOffset now) =>
        now - lastRebind >= DebounceWindow;
}
