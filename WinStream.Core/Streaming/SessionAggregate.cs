namespace WinStream.Core.Streaming;

public static class SessionAggregate
{
    /// <summary>
    /// Silent capture is a normal desktop state (nothing playing, muted app), so it
    /// never enters this calculation — only receiver session health does.
    /// </summary>
    public static SessionState Calculate(
        IReadOnlyCollection<SessionState> sessionStates,
        bool reconnectInProgress = false)
    {
        if (sessionStates.Count == 0)
        {
            return SessionState.Disconnected;
        }

        if (reconnectInProgress || sessionStates.Any(state => state == SessionState.Reconnecting))
        {
            return SessionState.Reconnecting;
        }

        if (sessionStates.Any(state => state == SessionState.Connecting))
        {
            return SessionState.Connecting;
        }

        var streaming = sessionStates.Count(state => state == SessionState.Streaming);
        var failed = sessionStates.Count(state =>
            state is SessionState.Failed or SessionState.Disconnected);
        if (streaming > 0 && failed > 0)
        {
            return SessionState.Degraded;
        }

        if (streaming == sessionStates.Count)
        {
            return SessionState.Streaming;
        }

        if (sessionStates.All(state => state == SessionState.Failed))
        {
            return SessionState.Failed;
        }

        return SessionState.Degraded;
    }
}
