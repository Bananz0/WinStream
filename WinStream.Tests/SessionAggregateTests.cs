using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class SessionAggregateTests
{
    [Fact]
    public void Partial_failure_is_degraded()
    {
        var state = SessionAggregate.Calculate(
            [SessionState.Streaming, SessionState.Failed]);

        Assert.Equal(SessionState.Degraded, state);
    }

    [Fact]
    public void All_streaming_is_streaming()
    {
        var state = SessionAggregate.Calculate(
            [SessionState.Streaming, SessionState.Streaming]);

        Assert.Equal(SessionState.Streaming, state);
    }

    [Fact]
    public void Silent_capture_is_not_part_of_session_health()
    {
        // A quiet desktop keeps streaming: only receiver session states can degrade.
        var state = SessionAggregate.Calculate(
            [SessionState.Streaming, SessionState.Streaming]);

        Assert.Equal(SessionState.Streaming, state);
    }

    [Fact]
    public void No_sessions_is_disconnected()
    {
        Assert.Equal(SessionState.Disconnected, SessionAggregate.Calculate([]));
    }

    [Fact]
    public void All_failed_is_failed()
    {
        var state = SessionAggregate.Calculate(
            [SessionState.Failed, SessionState.Failed]);

        Assert.Equal(SessionState.Failed, state);
    }

    [Fact]
    public void Reconnect_in_progress_wins()
    {
        var state = SessionAggregate.Calculate(
            [SessionState.Streaming],
            reconnectInProgress: true);

        Assert.Equal(SessionState.Reconnecting, state);
    }
}
