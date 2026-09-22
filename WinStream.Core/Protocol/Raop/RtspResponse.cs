using System.Globalization;

namespace WinStream.Core.Protocol.Raop;

public sealed class RtspResponse
{
    private RtspResponse(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        Body = body;
    }

    public int StatusCode { get; }

    public string ReasonPhrase { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public ReadOnlyMemory<byte> Body { get; }

    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    public string? SessionId
    {
        get
        {
            if (!Headers.TryGetValue("Session", out var value))
            {
                return null;
            }

            return value.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        }
    }

    public string? Transport =>
        Headers.TryGetValue("Transport", out var value) ? value : null;

    /// <summary>
    /// Raw <c>WWW-Authenticate</c> challenge, present when the receiver has an
    /// AirPlay password. Carries only a realm and nonce, never the password.
    /// </summary>
    public string? AuthenticationChallenge =>
        Headers.TryGetValue("WWW-Authenticate", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    /// <summary>The receiver rejected this request with RTSP 401.</summary>
    public bool IsUnauthorized => StatusCode == 401;

    public void EnsureSuccess(string method)
    {
        if (IsSuccessStatusCode)
        {
            return;
        }

        // Without this note a password-protected receiver reads as a pairing
        // failure, which sends the user back to re-typing a code that was fine.
        var hint = IsUnauthorized
            ? " The receiver is asking for its AirPlay password."
            : string.Empty;
        throw new InvalidOperationException(
            $"{method} failed with RTSP {StatusCode} {ReasonPhrase}.{hint}");
    }

    public static RtspResponse Parse(string headerText, ReadOnlyMemory<byte> body = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerText);
        var lines = headerText.Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.None);
        var statusParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 ||
            !statusParts[0].StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(statusParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new FormatException("Invalid RTSP status line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return new RtspResponse(
            statusCode,
            statusParts.Length == 3 ? statusParts[2] : string.Empty,
            headers,
            body);
    }
}
