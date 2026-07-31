namespace WinStream.Core;

/// <summary>
/// The Link media port, shared by discovery and the wire protocol.
/// </summary>
/// <remarks>
/// Lives above both Network and Protocol so discovery can fall back to the port without
/// importing WSL1 framing details. The control plane derives its own port from this in
/// <c>LinkControlProtocol</c>.
/// </remarks>
public static class LinkDefaults
{
    public const int MediaPort = 47200;
}
