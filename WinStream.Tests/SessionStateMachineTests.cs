using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class SessionStateMachineTests
{
    [Fact]
    public void HappyPath_ReachesStreamingThenDisconnected()
    {
        var machine = new SessionStateMachine();
        var changes = new List<SessionStateChanged>();
        machine.StateChanged += (_, change) => changes.Add(change);

        machine.TransitionTo(SessionState.Connecting);
        machine.TransitionTo(SessionState.Streaming);
        machine.TransitionTo(SessionState.Disconnecting);
        machine.TransitionTo(SessionState.Disconnected);

        Assert.Equal(SessionState.Disconnected, machine.State);
        Assert.Collection(
            changes,
            change => Assert.Equal(SessionState.Connecting, change.Current),
            change => Assert.Equal(SessionState.Streaming, change.Current),
            change => Assert.Equal(SessionState.Disconnecting, change.Current),
            change => Assert.Equal(SessionState.Disconnected, change.Current));
    }

    [Fact]
    public void Failure_CanRetry()
    {
        var machine = new SessionStateMachine();
        machine.TransitionTo(SessionState.Connecting);
        machine.TransitionTo(SessionState.Failed, "Receiver rejected ANNOUNCE.");

        machine.TransitionTo(SessionState.Connecting);

        Assert.Equal(SessionState.Connecting, machine.State);
    }

    [Fact]
    public void InvalidTransition_Throws()
    {
        var machine = new SessionStateMachine();

        var error = Assert.Throws<InvalidOperationException>(() =>
            machine.TransitionTo(SessionState.Streaming));

        Assert.Contains("Disconnected -> Streaming", error.Message);
    }

    [Fact]
    public void Streaming_CanEnterDegradedAndReconnecting()
    {
        var machine = new SessionStateMachine();
        machine.TransitionTo(SessionState.Connecting);
        machine.TransitionTo(SessionState.Streaming);
        machine.TransitionTo(SessionState.Degraded, "silent");
        machine.TransitionTo(SessionState.Reconnecting, "network");
        machine.TransitionTo(SessionState.Streaming);

        Assert.Equal(SessionState.Streaming, machine.State);
    }
}
