using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// AirPlay 2 control channel: clear pair-setup, then ChaCha20-Poly1305 framed RTSP.
/// </summary>
public sealed class EncryptedRtspClient : IAsyncDisposable
{
    private const string UserAgent = "AirPlay/415.3";

    private readonly string _host;
    private readonly int _port;
    private readonly TcpClient _tcp = new();
    private readonly string _dacpId =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly string _activeRemote =
        RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();
    private NetworkStream? _raw;
    private RtspCryptoStream? _crypto;
    private HkpTransient? _pairing;
    private int _cSeq;
    private bool _disposed;

    public EncryptedRtspClient(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        _host = host.Trim().TrimStart('[').TrimEnd(']');
        _port = port;
    }

    public HkpTransient Pairing =>
        _pairing ?? throw new InvalidOperationException("Not paired.");

    public int EventPort { get; private set; }

    public string SessionUuid { get; private set; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public string DeviceId { get; set; } = "AA:BB:CC:DD:EE:FF";

    public string LocalIp { get; set; } = "127.0.0.1";

    public async Task ConnectAndPairAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pairing is not null)
        {
            return;
        }

        await _tcp.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _raw = _tcp.GetStream();
        _pairing = await HkpPairSetupClient.PairAsync(_raw, _host, _port, cancellationToken)
            .ConfigureAwait(false);
        _crypto = new RtspCryptoStream(
            _raw,
            _pairing.ControlWriteKey.ToArray(),
            _pairing.ControlReadKey.ToArray());
    }

    public async Task GetInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var body = BinaryPlist.Write(new Dictionary<string, object>
        {
            ["qualifier"] = new List<object> { "txtAirPlay" }
        });
        var response = await SendAsync(
            "GET",
            "/info",
            new Dictionary<string, string>
            {
                ["X-Apple-ProtocolVersion"] = "1",
                ["Content-Type"] = "application/x-apple-binary-plist"
            },
            body,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess("GET /info");
    }

    public async Task SessionSetupAsync(CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var timingId = Guid.NewGuid().ToString().ToUpperInvariant();
        var setup = new Dictionary<string, object>
        {
            ["deviceID"] = DeviceId,
            ["macAddress"] = DeviceId,
            ["sessionUUID"] = SessionUuid,
            ["timingProtocol"] = "NTP",
            ["timingPort"] = 0L,
            ["name"] = "WinStream",
            ["model"] = "WinStream",
            ["sourceVersion"] = "415.3",
            ["osName"] = "Windows",
            ["osVersion"] = "10.0",
            ["osBuildVersion"] = "19041",
            ["groupUUID"] = timingId,
            ["groupContainsGroupLeader"] = false,
            ["isMultiSelectAirPlay"] = false,
            ["senderSupportsRelay"] = false
        };

        var response = await SendAsync(
            "SETUP",
            $"rtsp://{_host}/{SessionUuid}",
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-apple-binary-plist"
            },
            BinaryPlist.Write(setup),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess("SETUP session");

        if (response.Body.Length == 0)
        {
            throw new InvalidOperationException("Session SETUP returned an empty body.");
        }

        var plist = BinaryPlist.Read(response.Body.Span);
        if (!BinaryPlist.TryGetInteger(plist, "eventPort", out var eventPort) ||
            eventPort is <= 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Session SETUP response missing eventPort.");
        }

        EventPort = (int)eventPort;
    }

    public async Task<RtspResponse> SendAsync(
        string method,
        string target,
        IReadOnlyDictionary<string, string>? headers,
        byte[]? body,
        CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var request = BuildRequest(method, target, headers, body);
        await _crypto!.WritePlaintextAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var chunk = await _crypto!.ReadNextChunkAsync(cancellationToken).ConfigureAwait(false);
            message.Write(chunk);
            var snapshot = message.ToArray();
            if (!TrySplitHeaders(snapshot, out var headerLength))
            {
                continue;
            }

            var headerText = Encoding.ASCII.GetString(snapshot, 0, headerLength);
            var preliminary = ParseStatusAllowingHttp(headerText);
            var contentLength = preliminary.Headers.TryGetValue("Content-Length", out var value) &&
                                int.TryParse(value, out var parsed)
                ? parsed
                : 0;
            var totalNeeded = headerLength + contentLength;
            while (message.Length < totalNeeded)
            {
                var more = await _crypto.ReadNextChunkAsync(cancellationToken).ConfigureAwait(false);
                message.Write(more);
            }

            var full = message.ToArray();
            if (full.Length > totalNeeded)
            {
                Array.Resize(ref full, totalNeeded);
            }

            var body = contentLength == 0
                ? ReadOnlyMemory<byte>.Empty
                : full.AsMemory(headerLength, contentLength);
            return ParseStatusAllowingHttp(headerText, body);
        }
    }

    private byte[] BuildRequest(
        string method,
        string target,
        IReadOnlyDictionary<string, string>? headers,
        byte[]? body)
    {
        var builder = new StringBuilder()
            .Append(method).Append(' ').Append(target).Append(" RTSP/1.0\r\n")
            .Append("CSeq: ").Append(++_cSeq).Append("\r\n")
            .Append("User-Agent: ").Append(UserAgent).Append("\r\n")
            .Append("DACP-ID: ").Append(_dacpId).Append("\r\n")
            .Append("Active-Remote: ").Append(_activeRemote).Append("\r\n");

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        if (body is { Length: > 0 })
        {
            builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        builder.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        if (body is not { Length: > 0 })
        {
            return headerBytes;
        }

        var request = new byte[headerBytes.Length + body.Length];
        Buffer.BlockCopy(headerBytes, 0, request, 0, headerBytes.Length);
        Buffer.BlockCopy(body, 0, request, headerBytes.Length, body.Length);
        return request;
    }

    private static bool TrySplitHeaders(byte[] buffer, out int headerLength)
    {
        for (var i = 3; i < buffer.Length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' &&
                buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                headerLength = i + 1;
                return true;
            }
        }

        headerLength = 0;
        return false;
    }

    private static RtspResponse ParseStatusAllowingHttp(
        string headerText,
        ReadOnlyMemory<byte> body = default)
    {
        // Some receivers answer RTSP-style methods with HTTP/1.1 status lines.
        if (headerText.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            var rewritten = "RTSP/1.0" + headerText[headerText.IndexOf(' ')..];
            return RtspResponse.Parse(rewritten, body);
        }

        return RtspResponse.Parse(headerText, body);
    }

    private void EnsureCrypto()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_crypto is null || _pairing is null)
        {
            throw new InvalidOperationException("Call ConnectAndPairAsync first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _crypto?.Dispose();
        _pairing?.Dispose();
        if (_raw is not null)
        {
            await _raw.DisposeAsync().ConfigureAwait(false);
        }

        _tcp.Dispose();
        _disposed = true;
    }
}
