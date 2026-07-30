namespace WinStream.Core.Streaming;

public static class SessionAggregate
{
    public static SessionState Calculate(
        IReadOnlyCollection<SessionState> sessionStates,
        bool reconnectInProgress = false,
        bool captureSilentTooLong = false)
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
            return captureSilentTooLong ? SessionState.Degraded : SessionState.Streaming;
        }

        if (sessionStates.All(state => state == SessionState.Failed))
        {
            return SessionState.Failed;
        }

        return SessionState.Degraded;
    }
}
