using System.Net.Sockets;
using System.Text;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>HTTP pair-setup exchange for transient HKP over a clear TCP socket.</summary>
public static class HkpPairSetupClient
{
    public static async Task<HkpTransient> PairAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default,
        string? srpSecret = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        return await PairAsync(stream, host, port, cancellationToken, srpSecret).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="srpSecret"/> defaults to the fixed transient PIN. Receivers
    /// with an AirPlay password expect that password here instead.
    /// </summary>
    public static async Task<HkpTransient> PairAsync(
        Stream stream,
        string host,
        int port,
        CancellationToken cancellationToken = default,
        string? srpSecret = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var pairing = new HkpTransient(srpSecret);
        try
        {
            var m1 = HkpTransient.BuildM1();
            var m2 = await RoundTripAsync(stream, host, port, m1, cancellationToken)
                .ConfigureAwait(false);
            var m3 = pairing.ProcessM2AndBuildM3(m2);
            var m4 = await RoundTripAsync(stream, host, port, m3, cancellationToken)
                .ConfigureAwait(false);
            pairing.CompleteWithM4(m4);
            return pairing;
        }
        catch
        {
            pairing.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> RoundTripAsync(
        Stream stream,
        string host,
        int port,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(host, port, body);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var (status, responseBody) = await ReadHttpResponseAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (status is 470 or 403)
        {
            throw new InvalidOperationException(HkpTransient.DescribeHttpStatus(status));
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException(HkpTransient.DescribeHttpStatus(status));
        }

        if (responseBody.Length == 0)
        {
            throw new InvalidOperationException("Empty pair-setup response body.");
        }

        return responseBody;
    }

    private static byte[] BuildRequest(string host, int port, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            "POST /pair-setup HTTP/1.1\r\n" +
            $"Host: {host}:{port}\r\n" +
            "User-Agent: AirPlay/415.3\r\n" +
            "X-Apple-HKP: 4\r\n" +
            "Content-Type: application/pairing+tlv8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");
        var request = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, request, 0, header.Length);
        Buffer.BlockCopy(body, 0, request, header.Length, body.Length);
        return request;
    }

    private static async Task<(int Status, byte[] Body)> ReadHttpResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(512);
        var single = new byte[1];
        while (headerBytes.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Receiver closed the pair-setup connection.");
            }

            headerBytes.Add(single[0]);
            var count = headerBytes.Count;
            if (count >= 4 &&
                headerBytes[count - 4] == '\r' &&
                headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' &&
                headerBytes[count - 1] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var statusLine = headerText.Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : -1;

        var contentLength = 0;
        foreach (var line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed))
            {
                contentLength = parsed;
            }
        }

        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Receiver closed the pair-setup body.");
            }

            offset += read;
        }

        return (status, body);
    }
}
