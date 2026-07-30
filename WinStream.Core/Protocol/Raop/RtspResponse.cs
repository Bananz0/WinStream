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

    public void EnsureSuccess(string method)
    {
        if (!IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{method} failed with RTSP {StatusCode} {ReasonPhrase}.");
        }
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
