namespace WinStream.Core.Streaming;

public enum SessionState
{
    Disconnected,
    Connecting,
    Streaming,
    Disconnecting,
    Failed
}

public sealed record SessionStateChanged(
    SessionState Previous,
    SessionState Current,
    string? Reason = null);
