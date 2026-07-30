namespace WinStream.Core.Streaming;

public sealed class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<SessionState, SessionState[]> Allowed =
        new Dictionary<SessionState, SessionState[]>
        {
            [SessionState.Disconnected] = [SessionState.Connecting],
            [SessionState.Connecting] =
                [SessionState.Streaming, SessionState.Failed, SessionState.Disconnected],
            [SessionState.Streaming] =
                [SessionState.Disconnecting, SessionState.Failed],
            [SessionState.Disconnecting] =
                [SessionState.Disconnected, SessionState.Failed],
            [SessionState.Failed] =
                [SessionState.Connecting, SessionState.Disconnecting, SessionState.Disconnected]
        };

    public event EventHandler<SessionStateChanged>? StateChanged;

    public SessionState State { get; private set; } = SessionState.Disconnected;

    public void TransitionTo(SessionState next, string? reason = null)
    {
        if (State == next)
        {
            return;
        }

        if (!Allowed[State].Contains(next))
        {
            throw new InvalidOperationException(
                $"Invalid session transition: {State} -> {next}.");
        }

        var previous = State;
        State = next;
        StateChanged?.Invoke(
            this,
            new SessionStateChanged(previous, next, reason));
    }
}
