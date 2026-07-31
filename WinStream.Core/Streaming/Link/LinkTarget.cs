using System.Diagnostics.CodeAnalysis;
using System.Net;
using WinStream.Core.Protocol.Link;

namespace WinStream.Core.Streaming.Link;

/// <summary>
/// A Link companion address typed by the user: <c>host</c>, <c>host:mediaPort</c>,
/// or <c>[v6]:mediaPort</c>. The control port is always derived, never typed.
/// </summary>
public sealed record LinkTarget(string Host, int MediaPort)
{
    public int ControlPort => MediaPort + LinkControlProtocol.DefaultControlPortOffset;

    /// <summary>Credential and settings key — must stay stable across reconnects.</summary>
    public string Key => $"{Host}:{MediaPort}";

    public static bool TryParse(string? text, [NotNullWhen(true)] out LinkTarget? target)
    {
        target = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (!TrySplit(trimmed, out var host, out var port))
        {
            return false;
        }

        if (port < 1 || port >= IPEndPoint.MaxPort)
        {
            return false;
        }

        target = new LinkTarget(host, port);
        return true;
    }

    private static bool TrySplit(string text, out string host, out int port)
    {
        host = string.Empty;
        port = Wsl1Constants.DefaultMediaPort;

        if (text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close < 2)
            {
                return false;
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            return rest.Length == 0 || TryParsePortSuffix(rest, ref port);
        }

        // A bare IPv6 literal has several colons; only "host:port" has exactly one.
        var separator = text.IndexOf(':');
        if (separator < 0 || text.IndexOf(':', separator + 1) >= 0)
        {
            host = text;
            return host.Length > 0;
        }

        host = text[..separator];
        return host.Length > 0 && TryParsePortSuffix(text[separator..], ref port);
    }

    private static bool TryParsePortSuffix(string suffix, ref int port)
    {
        if (suffix.Length < 2 || suffix[0] != ':')
        {
            return false;
        }

        if (!int.TryParse(suffix[1..], out var parsed))
        {
            return false;
        }

        port = parsed;
        return true;
    }
}
