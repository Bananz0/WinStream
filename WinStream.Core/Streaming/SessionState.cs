namespace WinStream.Core.Streaming;

public enum SessionState
{
    Disconnected,
    Connecting,
    Streaming,
    Degraded,
    Reconnecting,
    Disconnecting,
    Failed
}

public sealed record SessionStateChanged(
    SessionState Previous,
    SessionState Current,
    string? Reason = null,
    bool UserRequested = false);
