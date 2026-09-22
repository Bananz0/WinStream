namespace WinStream.Core.Streaming;

public sealed class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<SessionState, SessionState[]> Allowed =
        new Dictionary<SessionState, SessionState[]>
        {
            [SessionState.Disconnected] =
                [SessionState.Connecting, SessionState.Reconnecting],
            [SessionState.Connecting] =
            [
                SessionState.Streaming,
                SessionState.Failed,
                SessionState.Disconnected,
                SessionState.Reconnecting
            ],
            [SessionState.Streaming] =
            [
                SessionState.Disconnecting,
                SessionState.Failed,
                SessionState.Degraded,
                SessionState.Reconnecting
            ],
            [SessionState.Degraded] =
            [
                SessionState.Streaming,
                SessionState.Reconnecting,
                SessionState.Disconnecting,
                SessionState.Failed,
                SessionState.Disconnected
            ],
            [SessionState.Reconnecting] =
            [
                SessionState.Streaming,
                SessionState.Degraded,
                SessionState.Failed,
                SessionState.Disconnected,
                SessionState.Disconnecting,
                SessionState.Connecting
            ],
            [SessionState.Disconnecting] =
                [SessionState.Disconnected, SessionState.Failed],
            [SessionState.Failed] =
            [
                SessionState.Connecting,
                SessionState.Disconnecting,
                SessionState.Disconnected,
                SessionState.Reconnecting
            ]
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

    public void Reset(SessionState state = SessionState.Disconnected)
    {
        var previous = State;
        State = state;
        if (previous != state)
        {
            StateChanged?.Invoke(
                this,
                new SessionStateChanged(previous, state));
        }
    }
}
