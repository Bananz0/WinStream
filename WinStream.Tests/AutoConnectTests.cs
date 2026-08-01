using WinStream.Core;
using WinStream.Core.Network;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class AutoConnectTests
{
    [Fact]
    public void Attempts_when_enabled_and_idle_with_a_remembered_receiver()
    {
        Assert.True(AutoConnectPolicy.ShouldAttempt(
            enabled: true,
            lastReceiverKey: "kitchen",
            sessionState: SessionState.Disconnected,
            connectionInFlight: false,
            attemptsAvailable: true));
    }

    [Fact]
    public void Does_not_attempt_while_Lab_responsiveness_is_selected()
    {
        Assert.False(AutoConnectPolicy.ShouldAttempt(
            enabled: true,
            lastReceiverKey: "kitchen",
            sessionState: SessionState.Disconnected,
            connectionInFlight: false,
            attemptsAvailable: true,
            responsiveness: PlaybackResponsiveness.LabPacket));
    }

    [Theory]
    [InlineData(false, "kitchen", SessionState.Disconnected, false, true)] // toggle off
    [InlineData(true, null, SessionState.Disconnected, false, true)] // nothing remembered
    [InlineData(true, "", SessionState.Disconnected, false, true)] // blank key
    [InlineData(true, "kitchen", SessionState.Streaming, false, true)] // already streaming
    [InlineData(true, "kitchen", SessionState.Connecting, false, true)] // connect underway
    [InlineData(true, "kitchen", SessionState.Reconnecting, false, true)] // recovering
    [InlineData(true, "kitchen", SessionState.Disconnected, true, true)] // another connect in flight
    [InlineData(true, "kitchen", SessionState.Disconnected, false, false)] // attempts exhausted
    public void Does_not_attempt_when_any_gate_is_closed(
        bool enabled,
        string? lastReceiverKey,
        SessionState sessionState,
        bool connectionInFlight,
        bool attemptsAvailable)
    {
        Assert.False(AutoConnectPolicy.ShouldAttempt(
            enabled,
            lastReceiverKey,
            sessionState,
            connectionInFlight,
            attemptsAvailable));
    }

    [Fact]
    public void FindTarget_matches_the_remembered_receiver_by_device_id()
    {
        var devices = new[]
        {
            new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.5", Port = 7000 },
            new DeviceInfo { DeviceID = "CC:DD", IPAddress = "10.0.0.6", Port = 7000 }
        };

        var target = AutoConnectPolicy.FindTarget(devices, "CC:DD");

        Assert.Same(devices[1], target);
    }

    [Fact]
    public void FindTarget_still_matches_after_the_address_changes()
    {
        var moved = new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.99", Port = 7000 };

        Assert.Same(moved, AutoConnectPolicy.FindTarget([moved], "AA:BB"));
    }

    [Fact]
    public void FindTarget_returns_null_when_the_receiver_is_absent_or_unknown()
    {
        var devices = new[] { new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.5", Port = 7000 } };

        Assert.Null(AutoConnectPolicy.FindTarget(devices, "ZZ:ZZ"));
        Assert.Null(AutoConnectPolicy.FindTarget(devices, null));
        Assert.Null(AutoConnectPolicy.FindTarget([], "AA:BB"));
    }

    [Fact]
    public void A_transient_failure_only_pauses_retries()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T12:00:00Z"));
        var tracker = new AutoConnectAttemptTracker(
            maxAttempts: 3,
            cooldown: TimeSpan.FromSeconds(15),
            timeProvider: time);

        Assert.True(tracker.AttemptsAvailable);

        tracker.RecordFailure();
        Assert.False(tracker.AttemptsAvailable);

        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(tracker.AttemptsAvailable);
    }

    [Fact]
    public void Repeated_failures_stop_retrying()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T12:00:00Z"));
        var tracker = new AutoConnectAttemptTracker(
            maxAttempts: 2,
            cooldown: TimeSpan.FromSeconds(15),
            timeProvider: time);

        tracker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(20));
        tracker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(20));

        Assert.False(tracker.AttemptsAvailable);
    }

    [Fact]
    public void Success_latches_off_and_reset_re_arms()
    {
        var tracker = new AutoConnectAttemptTracker();

        tracker.RecordSuccess();
        Assert.False(tracker.AttemptsAvailable);

        tracker.Reset();
        Assert.True(tracker.AttemptsAvailable);
    }

    [Fact]
    public void A_lost_session_re_arms_auto_connect_after_the_cooldown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T12:00:00Z"));
        var tracker = new AutoConnectAttemptTracker(
            maxAttempts: 3,
            cooldown: TimeSpan.FromSeconds(15),
            timeProvider: time);
        tracker.RecordSuccess();

        tracker.RecordSessionLost();
        Assert.False(tracker.AttemptsAvailable);

        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(tracker.AttemptsAvailable);
    }

    [Fact]
    public void A_lost_session_also_clears_earlier_failures()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T12:00:00Z"));
        var tracker = new AutoConnectAttemptTracker(
            maxAttempts: 2,
            cooldown: TimeSpan.FromSeconds(15),
            timeProvider: time);
        tracker.RecordFailure();
        tracker.RecordFailure();

        tracker.RecordSessionLost();
        time.Advance(TimeSpan.FromSeconds(15));

        Assert.True(tracker.AttemptsAvailable);
    }

    [Theory]
    [InlineData(SessionState.Streaming, SessionState.Disconnected)]
    [InlineData(SessionState.Streaming, SessionState.Failed)]
    [InlineData(SessionState.Degraded, SessionState.Disconnected)]
    [InlineData(SessionState.Degraded, SessionState.Failed)]
    [InlineData(SessionState.Reconnecting, SessionState.Disconnected)]
    [InlineData(SessionState.Reconnecting, SessionState.Failed)]
    public void An_established_session_that_drops_re_arms(SessionState previous, SessionState current)
    {
        Assert.True(AutoConnectPolicy.ReArmsAfterSessionEnd(
            previous,
            current,
            userInitiated: false));
    }

    [Fact]
    public void A_disconnect_the_user_asked_for_does_not_re_arm()
    {
        Assert.False(AutoConnectPolicy.ReArmsAfterSessionEnd(
            SessionState.Streaming,
            SessionState.Disconnected,
            userInitiated: true));
    }

    [Fact]
    public void A_user_disconnect_leaves_the_success_latch_off()
    {
        var tracker = new AutoConnectAttemptTracker();
        tracker.RecordSuccess();

        var reArms = AutoConnectPolicy.ReArmsAfterSessionEnd(
            SessionState.Streaming,
            SessionState.Disconnected,
            userInitiated: true);
        if (reArms)
        {
            tracker.RecordSessionLost();
        }

        Assert.False(reArms);
        Assert.False(tracker.AttemptsAvailable);
    }

    [Theory]
    [InlineData(SessionState.Connecting, SessionState.Failed)] // dial never established
    [InlineData(SessionState.Streaming, SessionState.Reconnecting)] // still recovering
    [InlineData(SessionState.Disconnected, SessionState.Connecting)] // ordinary connect
    public void Only_a_lost_session_re_arms(SessionState previous, SessionState current)
    {
        Assert.False(AutoConnectPolicy.ReArmsAfterSessionEnd(
            previous,
            current,
            userInitiated: false));
    }

    [Fact]
    public void Reset_clears_an_exhausted_budget()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-31T12:00:00Z"));
        var tracker = new AutoConnectAttemptTracker(
            maxAttempts: 1,
            cooldown: TimeSpan.FromSeconds(5),
            timeProvider: time);

        tracker.RecordFailure();
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(tracker.AttemptsAvailable);

        tracker.Reset();
        Assert.True(tracker.AttemptsAvailable);
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
