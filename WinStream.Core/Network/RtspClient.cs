#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Core.Network;

public sealed class RtspClient : IAsyncDisposable
{
    private const string UserAgent =
        "WinStream/1.0 (Windows; RAOP sender)";

    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly TcpClient _client = new();
    private readonly string _clientInstance =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private NetworkStream? _stream;
    private int _cSeq;
    private bool _disposed;

    public RtspClient(string serverHost, int serverPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serverPort);
        if (serverPort > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(serverPort));
        }

        _serverHost = NormalizeHost(serverHost);
        _serverPort = serverPort;
    }

    public string LocalIp =>
        (_client.Client.LocalEndPoint as IPEndPoint)?.Address.ToString()
        ?? throw new InvalidOperationException("RTSP client is not connected.");

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client.Connected)
        {
            return;
        }

        await _client.ConnectAsync(
            _serverHost,
            _serverPort,
            cancellationToken).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    public Task<RtspResponse> SendOptionsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync("OPTIONS", "*", null, null, cancellationToken);

    public Task<RtspResponse> SendAnnounceAsync(
        string target,
        string sdp,
        string appleChallenge,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            "ANNOUNCE",
            target,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/sdp",
                ["Apple-Challenge"] = appleChallenge
            },
            Encoding.ASCII.GetBytes(sdp),
            cancellationToken);

    public Task<RtspResponse> SendSetupAsync(
        string target,
        int clientRtpPort,
        int controlPort,
        int timingPort,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            "SETUP",
            target,
            new Dictionary<string, string>
            {
                ["Transport"] =
                    "RTP/AVP/UDP;unicast;mode=record;" +
                    $"client_port={clientRtpPort};" +
                    $"control_port={controlPort};" +
                    $"timing_port={timingPort}"
            },
            null,
            cancellationToken);

    public Task<RtspResponse> SendRecordAsync(
        string target,
        string sessionId,
        ushort sequenceNumber,
        uint rtpTimestamp,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            "RECORD",
            target,
            new Dictionary<string, string>
            {
                ["Session"] = sessionId,
                ["Range"] = "npt=0-",
                ["RTP-Info"] = $"seq={sequenceNumber};rtptime={rtpTimestamp}"
            },
            null,
            cancellationToken);

    public Task<RtspResponse> SendSetParameterAsync(
        string target,
        string sessionId,
        string parameterBody,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            "SET_PARAMETER",
            target,
            new Dictionary<string, string>
            {
                ["Session"] = sessionId,
                ["Content-Type"] = "text/parameters"
            },
            Encoding.ASCII.GetBytes(parameterBody),
            cancellationToken);

    public Task<RtspResponse> SendTeardownAsync(
        string target,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            "TEARDOWN",
            target,
            new Dictionary<string, string> { ["Session"] = sessionId },
            null,
            cancellationToken);

    private async Task<RtspResponse> SendAsync(
        string method,
        string target,
        IReadOnlyDictionary<string, string>? headers,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = _stream
            ?? throw new InvalidOperationException("RTSP client is not connected.");
        var request = BuildRequest(method, target, headers, body);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(stream, cancellationToken).ConfigureAwait(false);
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
            .Append("Client-Instance: ").Append(_clientInstance).Append("\r\n");

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

    private static async Task<RtspResponse> ReadResponseAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(512);
        var single = new byte[1];
        while (headerBytes.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Receiver closed the RTSP connection.");
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
        var preliminary = RtspResponse.Parse(headerText);
        var contentLength = preliminary.Headers.TryGetValue("Content-Length", out var value) &&
                            int.TryParse(value, out var parsed)
            ? parsed
            : 0;
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(
                body.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Receiver closed the RTSP response body.");
            }

            offset += read;
        }

        return RtspResponse.Parse(headerText, body);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _stream?.Dispose();
        _client.Dispose();
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']');
}
