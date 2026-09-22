namespace WinStream.Core.Streaming.Link;

public enum LinkSessionState
{
    Disconnected,
    Streaming,
    Failed
}

public sealed record LinkSessionStateChanged(
    LinkSessionState Previous,
    LinkSessionState Current,
    string? Reason = null);
