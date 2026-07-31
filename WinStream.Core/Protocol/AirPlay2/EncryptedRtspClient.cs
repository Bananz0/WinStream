using System.Net;
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
    private readonly SemaphoreSlim _requestGate = new(1, 1);
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

    public int DataPort { get; private set; }

    public int ControlPort { get; private set; }

    /// <summary>Receiver clock identity from session SETUP, when advertised.</summary>
    public ulong? RemoteClockId { get; private set; }

    public string SessionUuid { get; private set; } = Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>Stable sender MAC; must not be the receiver's deviceid.</summary>
    public string DeviceId { get; set; } = "AA:BB:CC:DD:EE:FF";

    /// <summary>Our address on the interface that reaches the receiver.</summary>
    public string LocalAddress =>
        _tcp.Client?.LocalEndPoint is IPEndPoint endpoint
            ? endpoint.Address.ToString()
            : "0.0.0.0";

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

    /// <summary>
    /// Session SETUP with PTP timing. This receiver answers 400 for NTP, so PTP
    /// is the only usable timing protocol here.
    /// </summary>
    /// <remarks>
    /// <c>ClockPorts</c> is mandatory even though it looks optional: it maps each
    /// peer address to the PTP port identity used toward that peer. Omit it and
    /// the receiver logs "remote port is unknown", declines to enable our clock
    /// port, and never sends us a single Sync — leaving the anchor stamped in the
    /// wrong timeline.
    /// </remarks>
    public async Task SessionSetupAsync(CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var response = await SendAsync(
            "SETUP",
            $"rtsp://{_host}/{SessionUuid}",
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-apple-binary-plist"
            },
            BinaryPlist.Write(BuildSessionSetupPayload(LocalAddress, _host, DeviceId, SessionUuid)),
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
        ParseTimingPeerInfo(plist);
    }

    /// <summary>Builds the session SETUP plist (testable without a live RTSP socket).</summary>
    internal static Dictionary<string, object> BuildSessionSetupPayload(
        string localAddress,
        string host,
        string deviceId,
        string sessionUuid)
    {
        var groupUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var clockId = unchecked((long)PtpClock.ClockIdFromDeviceId(deviceId));
        var peer = new Dictionary<string, object>
        {
            ["Addresses"] = new List<object> { localAddress },
            ["ID"] = deviceId,
            ["DeviceType"] = 0L,
            ["ClockID"] = clockId,
            ["ClockPorts"] = new Dictionary<string, object>
            {
                [localAddress] = (long)PtpClock.PortNumber,
                [host] = (long)PtpClock.PortNumber
            },
            ["SupportsClockPortMatchingOverride"] = true
        };

        return new Dictionary<string, object>
        {
            ["deviceID"] = deviceId,
            ["macAddress"] = deviceId,
            ["sessionUUID"] = sessionUuid,
            ["timingProtocol"] = "PTP",
            ["timingPeerInfo"] = peer,
            ["timingPeerList"] = new List<object> { peer },
            ["name"] = "WinStream",
            ["model"] = "WinStream",
            ["sourceVersion"] = "415.3",
            ["osName"] = "Windows",
            ["osVersion"] = "10.0",
            ["osBuildVersion"] = "19041",
            ["groupUUID"] = groupUuid,
            ["groupContainsGroupLeader"] = false,
            ["isMultiSelectAirPlay"] = false,
            ["senderSupportsRelay"] = false
        };
    }

    private void ParseTimingPeerInfo(object plist)
    {
        if (plist is not Dictionary<string, object?> root ||
            !root.TryGetValue("timingPeerInfo", out var raw) ||
            raw is not Dictionary<string, object?> peer)
        {
            return;
        }

        // ClockID is an EUI-64 bit pattern — accept any non-zero ulong encoding,
        // including values that look negative as signed long.
        if (peer.TryGetValue("ClockID", out var clockRaw) &&
            TryUInt64(clockRaw, out var clockId) &&
            clockId != 0)
        {
            RemoteClockId = clockId;
        }
    }

    private static bool TryUInt64(object? raw, out ulong value)
    {
        switch (raw)
        {
            case ulong u:
                value = u;
                return true;
            case long l:
                value = unchecked((ulong)l);
                return true;
            case int i:
                value = unchecked((ulong)i);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public async Task RecordAsync(CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var response = await SendAsync(
            "RECORD",
            $"rtsp://{_host}/{SessionUuid}",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess("RECORD");
    }

    public async Task StreamSetupAsync(
        int senderControlPort,
        byte[] audioSharedKey,
        CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        ArgumentNullException.ThrowIfNull(audioSharedKey);
        if (audioSharedKey.Length != 32)
        {
            throw new ArgumentException("shk must be 32 bytes.", nameof(audioSharedKey));
        }

        // Realtime type 0x60 / 96, ALAC — receiver hardcodes ALAC and ignores ct.
        var stream = new Dictionary<string, object>
        {
            ["type"] = 0x60L,
            ["audioFormat"] = 0x40000L,
            ["audioMode"] = "default",
            ["ct"] = 2L,
            ["isMedia"] = true,
            ["latencyMin"] = 11025L,
            ["latencyMax"] = 88200L,
            ["spf"] = 352L,
            ["sr"] = 44100L,
            ["controlPort"] = (long)senderControlPort,
            ["shk"] = audioSharedKey,
            ["supportsDynamicStreamID"] = false,
            ["streamConnectionID"] = (long)RandomNumberGenerator.GetInt32(1, int.MaxValue)
        };

        var body = BinaryPlist.Write(new Dictionary<string, object>
        {
            ["streams"] = new List<object> { stream }
        });

        var response = await SendAsync(
            "SETUP",
            $"rtsp://{_host}/{SessionUuid}",
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-apple-binary-plist"
            },
            body,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess("SETUP stream");

        if (response.Body.Length == 0)
        {
            throw new InvalidOperationException("Stream SETUP returned an empty body.");
        }

        var plist = BinaryPlist.Read(response.Body.Span);
        if (!BinaryPlist.TryGetStreamPorts(plist, out var dataPort, out var controlPort))
        {
            throw new InvalidOperationException(
                "Stream SETUP response missing dataPort/controlPort.");
        }

        DataPort = dataPort;
        ControlPort = controlPort;
    }

    public async Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var body = Encoding.ASCII.GetBytes($"volume: {volumeDb:0.000000}\r\n");
        var response = await SendAsync(
            "SET_PARAMETER",
            $"rtsp://{_host}/{SessionUuid}",
            new Dictionary<string, string>
            {
                ["Content-Type"] = "text/parameters"
            },
            body,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess("SET_PARAMETER volume");
    }

    /// <summary>
    /// Periodic sender heartbeat. Without it the receiver drops the session after
    /// roughly 30 seconds of control-channel silence.
    /// </summary>
    public async Task SendFeedbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        var response = await SendAsync(
            "POST",
            "/feedback",
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/x-apple-binary-plist"
            },
            BinaryPlist.Write(new Dictionary<string, object>()),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccess("POST /feedback");
    }

    public async Task TeardownAsync(CancellationToken cancellationToken = default)
    {
        EnsureCrypto();
        try
        {
            var body = BinaryPlist.Write(new Dictionary<string, object>());
            await SendAsync(
                "TEARDOWN",
                $"rtsp://{_host}/{SessionUuid}",
                new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-apple-binary-plist"
                },
                body,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown.
        }
    }

    public async Task<RtspResponse> SendAsync(
        string method,
        string target,
        IReadOnlyDictionary<string, string>? headers,
        byte[]? body,
        CancellationToken cancellationToken = default)
    {
        EnsureCrypto();

        // One request/response pair at a time: the keep-alive heartbeat and volume
        // changes would otherwise interleave frames on the same encrypted channel.
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = BuildRequest(method, target, headers, body);
            await _crypto!.WritePlaintextAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
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
        _requestGate.Dispose();
        _disposed = true;
    }
}
