using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinStream.Tools.RaopProbe;

/// <summary>
/// Low-level request dumper used to characterise what a receiver demands before
/// it will accept RTSP. Kept separate from the session path so it can poke at
/// endpoints the production client does not use.
/// </summary>
internal static class RawProbe
{
    public static async Task RunAsync(string host, int port)
    {
        Console.WriteLine($"\n== Raw probe {host}:{port} ==");

        await DumpAsync(host, port, "GET /info", BuildHttp("GET", "/info", host, port));

        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=');
        await DumpAsync(
            host,
            port,
            "OPTIONS with Apple-Challenge",
            BuildRtspOptions(host, port, challenge));

        await DumpAsync(
            host,
            port,
            "OPTIONS without Apple-Challenge",
            BuildRtspOptions(host, port, null));

        await DumpAsync(
            host,
            port,
            "GET /server-info",
            BuildHttp("GET", "/server-info", host, port));

        await DumpAsync(
            host,
            port,
            "POST /pair-setup",
            BuildHttp("POST", "/pair-setup", host, port, includeHkp: true));

        await DumpBinaryAsync(
            host,
            port,
            "POST /pair-setup (TLV8 transient M1 + X-Apple-HKP: 4)",
            BuildPairSetupM1(host, port, transient: true));

        await DumpBinaryAsync(
            host,
            port,
            "POST /pair-setup (TLV8 M1, non-transient + X-Apple-HKP: 4)",
            BuildPairSetupM1(host, port, transient: false));

        await DumpBinaryAsync(
            host,
            port,
            "POST /auth-setup (capability probe)",
            BuildAuthSetup(host, port));
    }

    /// <summary>
    /// HomeKit-style TLV8 pair-setup M1. Transient mode is what AirPlay 2 senders
    /// use when the receiver allows unauthenticated senders on the local network.
    /// </summary>
    private static byte[] BuildPairSetupM1(string host, int port, bool transient)
    {
        var tlv = new List<byte>
        {
            0x00, 0x01, 0x00, // kTLVType_Method = pair-setup
            0x06, 0x01, 0x01  // kTLVType_State = M1
        };

        if (transient)
        {
            // kTLVType_Flags = 0x00000010 (transient)
            tlv.AddRange([0x13, 0x04, 0x10, 0x00, 0x00, 0x00]);
        }

        return BuildBinaryRequest(host, port, "/pair-setup", tlv.ToArray(), includeHkp: true);
    }

    private static byte[] BuildAuthSetup(string host, int port)
    {
        // 0x01 = unencrypted mode byte, followed by a 32-byte curve25519 public key.
        var body = new byte[33];
        body[0] = 0x01;
        RandomNumberGenerator.Fill(body.AsSpan(1));
        return BuildBinaryRequest(host, port, "/auth-setup", body, includeHkp: false);
    }

    /// <summary>
    /// Transient pair-setup requires <c>X-Apple-HKP: 4</c>. Without it, modern
    /// macOS AirPlay Receiver returns 470 Connection Authorization Required.
    /// </summary>
    internal static string BuildPairSetupHeaders(string host, int port, int contentLength) =>
        BuildPostHeaders(host, port, "/pair-setup", contentLength, includeHkp: true);

    private static byte[] BuildBinaryRequest(
        string host,
        int port,
        string path,
        byte[] body,
        bool includeHkp)
    {
        var header = Encoding.ASCII.GetBytes(
            BuildPostHeaders(host, port, path, body.Length, includeHkp));
        var request = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, request, 0, header.Length);
        Buffer.BlockCopy(body, 0, request, header.Length, body.Length);
        return request;
    }

    private static string BuildPostHeaders(
        string host,
        int port,
        string path,
        int contentLength,
        bool includeHkp)
    {
        var builder = new StringBuilder()
            .Append("POST ").Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n")
            .Append("User-Agent: AirPlay/415.3\r\n");
        if (includeHkp)
        {
            builder.Append("X-Apple-HKP: 4\r\n");
        }

        return builder
            .Append("Content-Type: application/octet-stream\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n")
            .ToString();
    }

    /// <summary>Returns the RTSP status code for a bare OPTIONS, or -1 if unreachable.</summary>
    public static async Task<int> GetOptionsStatusAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(host, port, cts.Token);
            await using var stream = client.GetStream();

            var request = Encoding.ASCII.GetBytes(BuildRtspOptions(host, port, null));
            await stream.WriteAsync(request, cts.Token);
            await stream.FlushAsync(cts.Token);

            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                return -1;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var parts = text.Split(' ', 3);
            return parts.Length >= 2 && int.TryParse(parts[1], out var status) ? status : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string BuildHttp(
        string method,
        string path,
        string host,
        int port,
        bool includeHkp = false)
    {
        var builder = new StringBuilder()
            .Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n")
            .Append("User-Agent: AirPlay/415.3\r\n");
        if (includeHkp)
        {
            builder.Append("X-Apple-HKP: 4\r\n");
        }

        return builder
            .Append("Connection: close\r\n")
            .Append("Content-Length: 0\r\n")
            .Append("\r\n")
            .ToString();
    }

    private static string BuildRtspOptions(string host, int port, string? appleChallenge)
    {
        var builder = new StringBuilder()
            .Append("OPTIONS * RTSP/1.0\r\n")
            .Append("CSeq: 1\r\n")
            .Append("User-Agent: AirPlay/415.3\r\n")
            .Append("Client-Instance: 5CB4E1B1D2C3A4F5\r\n")
            .Append("DACP-ID: 5CB4E1B1D2C3A4F5\r\n")
            .Append("Active-Remote: 1986535575\r\n");

        if (appleChallenge is not null)
        {
            builder.Append("Apple-Challenge: ").Append(appleChallenge).Append("\r\n");
        }

        return builder.Append("\r\n").ToString();
    }

    private static Task DumpAsync(string host, int port, string label, string request) =>
        DumpBinaryAsync(host, port, label, Encoding.ASCII.GetBytes(request));

    private static async Task DumpBinaryAsync(string host, int port, string label, byte[] request)
    {
            Console.WriteLine($"\n--- {label} ---");
        if (label.Contains("pair-setup", StringComparison.OrdinalIgnoreCase))
        {
            var preview = Encoding.ASCII.GetString(request);
            var headerEnd = preview.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var requestHeaders = headerEnd >= 0 ? preview[..headerEnd] : preview;
            Console.WriteLine(
                requestHeaders.Contains("X-Apple-HKP: 4", StringComparison.Ordinal)
                    ? "request| X-Apple-HKP: 4 present"
                    : "request| X-Apple-HKP: 4 MISSING");
        }

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(host, port, cts.Token);
            await using var stream = client.GetStream();

            await stream.WriteAsync(request, cts.Token);
            await stream.FlushAsync(cts.Token);

            var buffer = new byte[16 * 1024];
            var total = 0;
            try
            {
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > 0 && ContainsHeaderEnd(buffer, total))
                    {
                        // Give the body a brief chance to arrive, then stop.
                        if (!stream.DataAvailable)
                        {
                            await Task.Delay(200, cts.Token);
                        }

                        if (!stream.DataAvailable)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Report whatever arrived before the timeout.
            }

            if (total == 0)
            {
                Console.WriteLine("(no response)");
                return;
            }

            Console.WriteLine(Describe(buffer.AsSpan(0, total)));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(failed: {ex.GetType().Name}: {ex.Message})");
        }
    }

    private static bool ContainsHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' &&
                buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        var text = Encoding.ASCII.GetString(data);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            builder.AppendLine(Printable(text));
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine(text[..headerEnd].Trim());

        var body = data[(headerEnd + 4)..];
        if (body.Length == 0)
        {
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"[body {body.Length} bytes]");

        // Binary plists carry readable key names; surface them for diagnosis.
        var tokens = ExtractTokens(body);
        if (tokens.Count > 0)
        {
            builder.AppendLine("  keys: " + string.Join(", ", tokens));
        }

        return builder.ToString().TrimEnd();
    }

    private static List<string> ExtractTokens(ReadOnlySpan<byte> body)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var b in body)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                current.Append((char)b);
                continue;
            }

            if (current.Length >= 4)
            {
                tokens.Add(current.ToString());
            }

            current.Clear();
        }

        if (current.Length >= 4)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string Printable(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is '\r' or '\n' || (c >= ' ' && c < (char)0x7F) ? c : '.');
        }

        return builder.ToString();
    }
}
