using WinStream.Core.Logging;
using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Streaming.Link;

namespace WinStream.Core.Streaming;

/// <summary>
/// Owns automatic reconnection to the remembered receiver: the retry budget, the
/// re-arm after a lost session, and the gates a discovery pass has to clear. Kept
/// free of WinUI so the composition of policy + tracker is unit-testable.
/// </summary>
public sealed class AutoConnectCoordinator
{
    private readonly AutoConnectAttemptTracker _attempts;

    public AutoConnectCoordinator(AutoConnectAttemptTracker? attempts = null)
    {
        _attempts = attempts ?? new AutoConnectAttemptTracker();
    }

    /// <summary>Re-arms after the user toggles auto-connect or remembers a new receiver.</summary>
    public void Reset() => _attempts.Reset();

    public void RecordSuccess() => _attempts.RecordSuccess();

    public void RecordFailure() => _attempts.RecordFailure();

    /// <summary>
    /// Reports an aggregate state change. A session that ended on its own makes
    /// auto-connect eligible again; a disconnect the user asked for does not.
    /// Intent is read from <see cref="SessionStateChanged.UserRequested"/> on this
    /// transition — never from a process-wide sticky latch.
    /// </summary>
    public void NoteStateChange(SessionStateChanged change)
    {
        if (!AutoConnectPolicy.ReArmsAfterSessionEnd(
                change.Previous,
                change.Current,
                change.UserRequested))
        {
            return;
        }

        _attempts.RecordSessionLost();
        AppLog.Info("auto-connect", $"Session ended ({change.Current}); auto-connect re-armed.");
    }

    /// <summary>
    /// The remembered receiver to dial on this discovery pass, or <c>null</c> when any
    /// gate is closed (setting off, wrong sink, Extreme, budget spent, not discovered).
    /// </summary>
    public DeviceInfo? ResolveTarget(
        AppSettings settings,
        IEnumerable<DeviceInfo> discovered,
        SessionState sessionState,
        bool connectionInFlight)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(discovered);

        if (!SinkModeSwitchPolicy.AllowsAirPlayAutoConnect(settings.SinkMode))
        {
            return null;
        }

        return AutoConnectPolicy.ShouldAttempt(
            settings.AutoConnectLastReceiver,
            settings.LastReceiverKey,
            sessionState,
            connectionInFlight,
            _attempts.AttemptsAvailable,
            settings.PlaybackResponsiveness)
            ? AutoConnectPolicy.FindTarget(discovered, settings.LastReceiverKey)
            : null;
    }
}
