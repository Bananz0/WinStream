#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Protocol.Raop;
using WinStream.Core.Streaming;
using WinStream.Network;

namespace WinStream.Streaming;

public sealed class RaopSession : IAirPlaySession
{
    private const int FramesPerPacket = 352;

    private readonly DeviceInfo _receiver;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SessionStateMachine _stateMachine = new();
    private RtspClient? _rtspClient;
    private UdpClient? _audioSocket;
    private UdpClient? _controlSocket;
    private UdpClient? _timingSocket;
    private string? _sessionId;
    private bool _disposed;

    public RaopSession(DeviceInfo receiver)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        ReceiverId = !string.IsNullOrWhiteSpace(receiver.DeviceID)
            ? receiver.DeviceID
            : $"{receiver.IPAddress}:{receiver.Port}";
        _stateMachine.StateChanged += (_, change) =>
            StateChanged?.Invoke(this, change);
    }

    public event EventHandler<SessionStateChanged>? StateChanged;

    public string ReceiverId { get; }

    public SessionState State => _stateMachine.State;

    public RaopEncryptionMaterial? EncryptionMaterial { get; private set; }

    public RaopTransportInfo? TransportInfo { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SessionState.Streaming)
            {
                return;
            }

            ValidateReceiver();
            ChangeState(SessionState.Connecting);
            try
            {
                var address = IPAddress.Parse(NormalizeHost(_receiver.IPAddress));
                _audioSocket = BindUdpSocket(address.AddressFamily);
                _controlSocket = BindUdpSocket(address.AddressFamily);
                _timingSocket = BindUdpSocket(address.AddressFamily);
                EncryptionMaterial = RaopCrypto.CreateEncryptionMaterial(_receiver.PublicKey);

                _rtspClient = new RtspClient(_receiver.IPAddress, _receiver.Port);
                await _rtspClient.ConnectAsync(cancellationToken).ConfigureAwait(false);

                var options = await _rtspClient
                    .SendOptionsAsync(cancellationToken)
                    .ConfigureAwait(false);
                options.EnsureSuccess("OPTIONS");

                var streamId = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();
                var targetBase = $"rtsp://{FormatHost(_receiver.IPAddress)}/{streamId}";
                var sdp = BuildSdp(
                    _rtspClient.LocalIp,
                    _receiver.IPAddress,
                    streamId,
                    EncryptionMaterial);
                var challenge = Convert.ToBase64String(
                    RandomNumberGenerator.GetBytes(16)).TrimEnd('=');
                var announce = await _rtspClient.SendAnnounceAsync(
                    targetBase,
                    sdp,
                    challenge,
                    cancellationToken).ConfigureAwait(false);
                announce.EnsureSuccess("ANNOUNCE");

                var setup = await _rtspClient.SendSetupAsync(
                    $"{targetBase}/stream/track1",
                    GetLocalPort(_audioSocket),
                    GetLocalPort(_controlSocket),
                    GetLocalPort(_timingSocket),
                    cancellationToken).ConfigureAwait(false);
                setup.EnsureSuccess("SETUP");
                _sessionId = setup.SessionId
                    ?? throw new InvalidOperationException(
                        "Receiver did not return an RTSP Session header.");
                TransportInfo = RaopTransportInfo.Parse(setup.Transport);

                var record = await _rtspClient.SendRecordAsync(
                    $"{targetBase}/stream",
                    _sessionId,
                    cancellationToken).ConfigureAwait(false);
                record.EnsureSuccess("RECORD");
                ChangeState(SessionState.Streaming);
            }
            catch (Exception ex)
            {
                await ReleaseResourcesAsync().ConfigureAwait(false);
                ChangeState(SessionState.Failed, ex.Message);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SessionState.Disconnected)
            {
                return;
            }

            ChangeState(SessionState.Disconnecting);
            if (_rtspClient is not null && !string.IsNullOrWhiteSpace(_sessionId))
            {
                try
                {
                    await _rtspClient.SendTeardownAsync(
                        $"rtsp://{FormatHost(_receiver.IPAddress)}/stream",
                        _sessionId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // TEARDOWN is best effort; local resources must always be released.
                }
            }

            await ReleaseResourcesAsync().ConfigureAwait(false);
            ChangeState(SessionState.Disconnected);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _disposed = true;
    }

    private async Task ReleaseResourcesAsync()
    {
        if (_rtspClient is not null)
        {
            await _rtspClient.DisposeAsync().ConfigureAwait(false);
            _rtspClient = null;
        }

        _audioSocket?.Dispose();
        _audioSocket = null;
        _controlSocket?.Dispose();
        _controlSocket = null;
        _timingSocket?.Dispose();
        _timingSocket = null;
        _sessionId = null;
        TransportInfo = null;
    }

    private void ValidateReceiver()
    {
        if (string.IsNullOrWhiteSpace(_receiver.IPAddress))
        {
            throw new InvalidOperationException("Receiver has no IP address.");
        }

        if (_receiver.Port is <= 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Receiver has an invalid RTSP port.");
        }

        if (string.IsNullOrWhiteSpace(_receiver.PublicKey))
        {
            throw new InvalidOperationException(
                "Receiver did not advertise the RSA public key required by classic RAOP.");
        }
    }

    private void ChangeState(SessionState state, string? reason = null)
    {
        _stateMachine.TransitionTo(state, reason);
    }

    private static UdpClient BindUdpSocket(AddressFamily addressFamily)
    {
        var socket = new UdpClient(addressFamily);
        var any = addressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any;
        socket.Client.Bind(new IPEndPoint(any, 0));
        return socket;
    }

    private static int GetLocalPort(UdpClient client) =>
        ((IPEndPoint)client.Client.LocalEndPoint!).Port;

    private static string BuildSdp(
        string localIp,
        string serverIp,
        string streamId,
        RaopEncryptionMaterial encryption)
    {
        var addressType = IPAddress.Parse(NormalizeHost(localIp)).AddressFamily ==
                          AddressFamily.InterNetworkV6
            ? "IP6"
            : "IP4";
        return
            "v=0\r\n" +
            $"o=WinStream {streamId} 0 IN {addressType} {NormalizeHost(localIp)}\r\n" +
            "s=WinStream\r\n" +
            $"c=IN {addressType} {NormalizeHost(serverIp)}\r\n" +
            "t=0 0\r\n" +
            "m=audio 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 AppleLossless\r\n" +
            $"a=fmtp:96 {FramesPerPacket} 0 16 40 10 14 2 255 0 0 44100\r\n" +
            $"a=rsaaeskey:{encryption.EncryptedAesKeyBase64}\r\n" +
            $"a=aesiv:{encryption.AesIvBase64}\r\n";
    }

    private static string FormatHost(string host)
    {
        var normalized = NormalizeHost(host);
        return IPAddress.TryParse(normalized, out var address) &&
               address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{normalized}]"
            : normalized;
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']');
}
