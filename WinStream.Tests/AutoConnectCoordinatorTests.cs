using WinStream.Core;
using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Composes the pure policy + tracker the way MainWindow does, so a lost session
/// becoming eligible again cannot silently stop resolving a target.
/// </summary>
public class AutoConnectCoordinatorTests
{
    private static AppSettings Settings(
        bool enabled = true,
        string? lastKey = "AA:BB",
        SinkMode sink = SinkMode.AirPlay,
        PlaybackResponsiveness responsiveness = PlaybackResponsiveness.Auto) => new()
        {
            AutoConnectLastReceiver = enabled,
            LastReceiverKey = lastKey,
            SinkMode = sink,
            PlaybackResponsiveness = responsiveness
        };

    private static DeviceInfo Device(string id = "AA:BB") => new()
    {
        DeviceID = id,
        IPAddress = "10.0.0.5",
        Port = 7000,
        DisplayName = "living-room"
    };

    [Fact]
    public void ResolveTarget_returns_the_remembered_receiver_when_idle()
    {
        var coordinator = new AutoConnectCoordinator();
        var target = Device();

        Assert.Same(
            target,
            coordinator.ResolveTarget(
                Settings(),
                [target],
                SessionState.Disconnected,
                connectionInFlight: false));
    }

    [Fact]
    public void ResolveTarget_stays_null_after_a_successful_connect_until_the_session_is_lost()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        var coordinator = new AutoConnectCoordinator(
            new AutoConnectAttemptTracker(
                sessionLostCooldown: TimeSpan.FromSeconds(5),
                timeProvider: time));
        var target = Device();
        coordinator.RecordSuccess();

        Assert.Null(coordinator.ResolveTarget(
            Settings(),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));

        coordinator.NoteStateChange(
            new SessionStateChanged(
                SessionState.Streaming,
                SessionState.Failed,
                "keep-alive",
                UserRequested: false));
        Assert.Null(coordinator.ResolveTarget(
            Settings(),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Same(
            target,
            coordinator.ResolveTarget(
                Settings(),
                [target],
                SessionState.Disconnected,
                connectionInFlight: false));
    }

    [Fact]
    public void A_user_disconnect_never_re_arms_even_after_the_cooldown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        var coordinator = new AutoConnectCoordinator(
            new AutoConnectAttemptTracker(
                sessionLostCooldown: TimeSpan.FromSeconds(5),
                timeProvider: time));
        var target = Device();
        coordinator.RecordSuccess();

        coordinator.NoteStateChange(
            new SessionStateChanged(
                SessionState.Streaming,
                SessionState.Disconnected,
                Reason: null,
                UserRequested: true));
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(coordinator.ResolveTarget(
            Settings(),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));
    }

    [Fact]
    public void Partial_user_remove_then_lost_session_re_arms_after_cooldown()
    {
        // Multi-room: user removes room A (no terminal UserRequested). Later room B dies
        // with UserRequested=false — must re-arm, not inherit a sticky suppress latch.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
        var coordinator = new AutoConnectCoordinator(
            new AutoConnectAttemptTracker(
                sessionLostCooldown: TimeSpan.FromSeconds(5),
                timeProvider: time));
        var target = Device();
        coordinator.RecordSuccess();

        var partialIntent = SessionEndIntent.UserRequested(
            userDisconnectApi: true,
            sessionsRemain: true);
        Assert.False(partialIntent);
        coordinator.NoteStateChange(
            new SessionStateChanged(
                SessionState.Streaming,
                SessionState.Streaming,
                "removed",
                UserRequested: partialIntent));
        Assert.Null(coordinator.ResolveTarget(
            Settings(),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));

        coordinator.NoteStateChange(
            new SessionStateChanged(
                SessionState.Streaming,
                SessionState.Disconnected,
                "Receiver ended the session.",
                UserRequested: SessionEndIntent.UserRequested(
                    userDisconnectApi: false,
                    sessionsRemain: false)));

        Assert.Null(coordinator.ResolveTarget(
            Settings(),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Same(
            target,
            coordinator.ResolveTarget(
                Settings(),
                [target],
                SessionState.Disconnected,
                connectionInFlight: false));
    }

    [Fact]
    public void ResolveTarget_respects_Extreme_and_Link_gates()
    {
        var coordinator = new AutoConnectCoordinator();
        var target = Device();

        Assert.Null(coordinator.ResolveTarget(
            Settings(responsiveness: PlaybackResponsiveness.LabPacket),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));
        Assert.Null(coordinator.ResolveTarget(
            Settings(sink: SinkMode.Link),
            [target],
            SessionState.Disconnected,
            connectionInFlight: false));
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
