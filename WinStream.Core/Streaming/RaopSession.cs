#nullable enable

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Protocol.Raop;
using WinStream.Network;
using WinStream.Networking;

namespace WinStream.Core.Streaming;

public sealed class RaopSession : IAirPlaySession
{
    private const int FramesPerPacket = AlacEncoder.FramesPerPacket;
    private const uint DefaultLatencyFrames = 88200; // ~2s at 44.1 kHz

    private readonly DeviceInfo _receiver;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _rtspGate = new(1, 1);
    private readonly SessionStateMachine _stateMachine = new();
    private readonly PcmPacketBuffer _pcmBuffer = new();
    private readonly ConcurrentDictionary<ushort, byte[]> _sentPackets = new();
    private readonly object _mediaGate = new();
    private RtspClient? _rtspClient;
    private UdpClient? _audioSocket;
    private UdpClient? _controlSocket;
    private UdpClient? _timingSocket;
    private AesAudioEncryptor? _encryptor;
    private CancellationTokenSource? _mediaCts;
    private Task? _controlLoop;
    private Task? _timingLoop;
    private Task? _syncLoop;
    private IPEndPoint? _audioEndpoint;
    private IPEndPoint? _controlEndpoint;
    private IPEndPoint? _timingEndpoint;
    private string? _sessionId;
    private string? _streamTarget;
    private ushort _sequenceNumber;
    private uint _rtpTimestamp;
    private uint _ssrc;
    private bool _sendMarker = true;
    private bool _firstSync = true;
    private bool _rtpBasePending = true;
    private float _volumeDb = -20f;
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

    private RaopEncryptionMaterial? _encryptionMaterial;

    private RaopTransportInfo? _transportInfo;

    private RaopEncryptionMode _encryptionMode = RaopEncryptionMode.Rsa;

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
                UdpSocketConfigurer.SuppressUdpConnReset(_audioSocket);
                UdpSocketConfigurer.SuppressUdpConnReset(_controlSocket);
                UdpSocketConfigurer.SuppressUdpConnReset(_timingSocket);
                _encryptionMode = AirPlayCapability.ResolveEncryptionMode(
                    _receiver.EncryptionTypes);
                if (_encryptionMode == RaopEncryptionMode.Rsa)
                {
                    _encryptionMaterial = RaopCrypto.CreateEncryptionMaterial();
                    _encryptor = new AesAudioEncryptor(
                        _encryptionMaterial.AesKey,
                        _encryptionMaterial.AesIv);
                }
                else
                {
                    _encryptionMaterial = null;
                    _encryptor = null;
                }

                _rtspClient = new RtspClient(_receiver.IPAddress, _receiver.Port);
                await _rtspClient.ConnectAsync(cancellationToken).ConfigureAwait(false);

                var options = await _rtspClient
                    .SendOptionsAsync(cancellationToken)
                    .ConfigureAwait(false);
                options.EnsureSuccess("OPTIONS");

                var streamId = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();
                var targetBase = $"rtsp://{FormatHost(_receiver.IPAddress)}/{streamId}";
                _streamTarget = $"{targetBase}/stream";
                var sdp = BuildSdp(
                    _rtspClient.LocalIp,
                    _receiver.IPAddress,
                    streamId,
                    _encryptionMaterial);
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
                _transportInfo = RaopTransportInfo.Parse(setup.Transport);
                BindRemoteEndpoints(address, _transportInfo);

                _sequenceNumber = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue);
                _rtpTimestamp = (uint)RandomNumberGenerator.GetInt32(0, int.MaxValue);
                _ssrc = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                _sendMarker = true;
                _firstSync = true;
                _rtpBasePending = true;
                _pcmBuffer.Reset();

                var record = await _rtspClient.SendRecordAsync(
                    _streamTarget,
                    _sessionId,
                    _sequenceNumber,
                    _rtpTimestamp,
                    cancellationToken).ConfigureAwait(false);
                record.EnsureSuccess("RECORD");

                await SendVolumeInternalAsync(_volumeDb, cancellationToken).ConfigureAwait(false);
                StartMediaLoops();
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

    public void SubmitPcm(
        ReadOnlyMemory<byte> pcm,
        AudioFormat format,
        uint? sharedMediaTimestamp = null)
    {
        if (State != SessionState.Streaming ||
            _audioSocket is null ||
            _audioEndpoint is null)
        {
            return;
        }

        byte[][] packets;
        lock (_mediaGate)
        {
            SharedMediaClockAlignment.Freeze(
                ref _rtpTimestamp,
                ref _rtpBasePending,
                sharedMediaTimestamp);

            packets = new System.Collections.Generic.List<byte[]>(
                _pcmBuffer.Push(pcm.Span, format)).ToArray();
        }

        foreach (var packetPcm in packets)
        {
            SendAudioPacket(packetPcm);
        }
    }

    public async Task SetVolumeAsync(
        float volumeDb,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volumeDb = Math.Clamp(volumeDb, -144f, 0f);
        if (State != SessionState.Streaming ||
            _rtspClient is null ||
            string.IsNullOrWhiteSpace(_sessionId) ||
            string.IsNullOrWhiteSpace(_streamTarget))
        {
            return;
        }

        await SendVolumeInternalAsync(_volumeDb, cancellationToken).ConfigureAwait(false);
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
            await StopMediaLoopsAsync().ConfigureAwait(false);
            if (_rtspClient is not null && !string.IsNullOrWhiteSpace(_sessionId))
            {
                try
                {
                    await _rtspClient.SendTeardownAsync(
                        _streamTarget ?? $"rtsp://{FormatHost(_receiver.IPAddress)}/stream",
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
        _rtspGate.Dispose();
        _disposed = true;
    }

    private void SendAudioPacket(byte[] pcmPacket)
    {
        if (_audioSocket is null || _audioEndpoint is null)
        {
            return;
        }

        Span<byte> alac = stackalloc byte[AlacEncoder.GetMaxEncodedLength(pcmPacket.Length)];
        var alacLength = AlacEncoder.Encode(pcmPacket, alac);
        var payload = alac[..alacLength].ToArray();
        _encryptor?.EncryptInPlace(payload);

        ushort sequence;
        uint timestamp;
        bool marker;
        lock (_mediaGate)
        {
            sequence = _sequenceNumber++;
            timestamp = _rtpTimestamp;
            _rtpTimestamp += (uint)FramesPerPacket;
            marker = _sendMarker;
            _sendMarker = false;
        }

        Span<byte> packet = stackalloc byte[12 + payload.Length];
        var length = RtpPacketizer.WriteAudioPacket(
            packet,
            sequence,
            timestamp,
            _ssrc,
            payload,
            marker);
        var bytes = packet[..length].ToArray();
        _sentPackets[sequence] = bytes;
        TrimPacketCache(sequence);

        try
        {
            _ = _audioSocket.SendAsync(bytes, _audioEndpoint);
        }
        catch
        {
            // Transient UDP send failures are ignored; reconnect handled in later phases.
        }
    }

    private void TrimPacketCache(ushort latest)
    {
        // Keep roughly the last second of packets for retransmission.
        var minKeep = (ushort)(latest - 100);
        foreach (var key in _sentPackets.Keys)
        {
            var age = (ushort)(latest - key);
            if (age > 100 && key < minKeep)
            {
                _sentPackets.TryRemove(key, out _);
            }
        }
    }

    private void StartMediaLoops()
    {
        _mediaCts = new CancellationTokenSource();
        var token = _mediaCts.Token;
        _controlLoop = Task.Run(() => RunControlLoopAsync(token), token);
        _timingLoop = Task.Run(() => RunTimingLoopAsync(token), token);
        _syncLoop = Task.Run(() => RunSyncLoopAsync(token), token);
    }

    private async Task StopMediaLoopsAsync()
    {
        if (_mediaCts is null)
        {
            return;
        }

        try
        {
            await _mediaCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        await Task.WhenAll(
            WaitSoft(_controlLoop),
            WaitSoft(_timingLoop),
            WaitSoft(_syncLoop)).ConfigureAwait(false);
        _mediaCts.Dispose();
        _mediaCts = null;
        _controlLoop = null;
        _timingLoop = null;
        _syncLoop = null;
    }

    private async Task RunControlLoopAsync(CancellationToken cancellationToken)
    {
        if (_controlSocket is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _controlSocket
                    .ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (RtpPacketizer.TryReadResendRequest(
                        result.Buffer,
                        out var missed,
                        out var count))
                {
                    for (ushort i = 0; i < count; i++)
                    {
                        var seq = (ushort)(missed + i);
                        if (_sentPackets.TryGetValue(seq, out var packet))
                        {
                            await _controlSocket
                                .SendAsync(packet, result.RemoteEndPoint, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep listening while the session is alive.
            }
        }
    }

    private async Task RunTimingLoopAsync(CancellationToken cancellationToken)
    {
        if (_timingSocket is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _timingSocket
                    .ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                var receivedNtp = NtpTime.Now();
                if (!RtpPacketizer.TryReadTimingRequest(
                        result.Buffer,
                        out var sequence,
                        out var sendNtp))
                {
                    continue;
                }

                var response = new byte[32];
                var length = RtpPacketizer.WriteTimingResponse(
                    response,
                    sequence,
                    sendNtp,
                    receivedNtp,
                    NtpTime.Now());
                await _timingSocket
                    .SendAsync(response.AsMemory(0, length), result.RemoteEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep listening while the session is alive.
            }
        }
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        if (_controlSocket is null || _controlEndpoint is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                uint now;
                bool first;
                lock (_mediaGate)
                {
                    now = _rtpTimestamp;
                    first = _firstSync;
                    _firstSync = false;
                }

                var nowMinusLatency = now - DefaultLatencyFrames;
                var packet = new byte[20];
                var length = RtpPacketizer.WriteSyncPacket(
                    packet,
                    nowMinusLatency,
                    NtpTime.Now(),
                    now,
                    first);
                await _controlSocket
                    .SendAsync(packet.AsMemory(0, length), _controlEndpoint, cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep syncing while the session is alive.
            }
        }
    }

    private async Task SendVolumeInternalAsync(
        float volumeDb,
        CancellationToken cancellationToken)
    {
        if (_rtspClient is null ||
            string.IsNullOrWhiteSpace(_sessionId) ||
            string.IsNullOrWhiteSpace(_streamTarget))
        {
            return;
        }

        await _rtspGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = $"volume: {volumeDb:0.000000}\r\n";
            var response = await _rtspClient
                .SendSetParameterAsync(_streamTarget, _sessionId, body, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccess("SET_PARAMETER");
        }
        finally
        {
            _rtspGate.Release();
        }
    }

    private void BindRemoteEndpoints(IPAddress address, RaopTransportInfo transport)
    {
        if (transport.ServerPort is null or <= 0)
        {
            throw new InvalidOperationException("SETUP response omitted server_port.");
        }

        _audioEndpoint = new IPEndPoint(address, transport.ServerPort.Value);
        _controlEndpoint = new IPEndPoint(
            address,
            transport.ControlPort ?? transport.ServerPort.Value);
        _timingEndpoint = new IPEndPoint(
            address,
            transport.TimingPort ?? transport.ServerPort.Value);
    }

    private async Task ReleaseResourcesAsync()
    {
        await StopMediaLoopsAsync().ConfigureAwait(false);
        _sentPackets.Clear();
        _pcmBuffer.Reset();
        _encryptor?.Dispose();
        _encryptor = null;
        if (_encryptionMaterial is not null)
        {
            CryptographicOperations.ZeroMemory(_encryptionMaterial.AesKey);
            CryptographicOperations.ZeroMemory(_encryptionMaterial.AesIv);
            _encryptionMaterial = null;
        }

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
        _streamTarget = null;
        _transportInfo = null;
        _audioEndpoint = null;
        _controlEndpoint = null;
        _timingEndpoint = null;
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

        if (AirPlayCapability.ResolveEncryptionMode(_receiver.EncryptionTypes) ==
            RaopEncryptionMode.Unsupported)
        {
            throw new InvalidOperationException(
                "Receiver does not advertise classic RAOP encryption (et=0 or et=1). " +
                "If this is a Mac, set AirPlay Receiver to Everyone (or anyone on the same network).");
        }
    }

    private void ChangeState(SessionState state, string? reason = null)
    {
        _stateMachine.TransitionTo(state, reason);
    }

    private static async Task WaitSoft(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation / dispose races are expected.
        }
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
        RaopEncryptionMaterial? encryption)
    {
        var addressType = IPAddress.Parse(NormalizeHost(localIp)).AddressFamily ==
                          AddressFamily.InterNetworkV6
            ? "IP6"
            : "IP4";
        var sdp =
            "v=0\r\n" +
            $"o=WinStream {streamId} 0 IN {addressType} {NormalizeHost(localIp)}\r\n" +
            "s=WinStream\r\n" +
            $"c=IN {addressType} {NormalizeHost(serverIp)}\r\n" +
            "t=0 0\r\n" +
            "m=audio 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 AppleLossless\r\n" +
            $"a=fmtp:96 {FramesPerPacket} 0 16 40 10 14 2 255 0 0 44100\r\n";

        // et=0 receivers expect clear ALAC: omitting the key attributes signals it.
        if (encryption is not null)
        {
            sdp +=
                $"a=rsaaeskey:{encryption.EncryptedAesKeyBase64}\r\n" +
                $"a=aesiv:{encryption.AesIvBase64}\r\n";
        }

        return sdp;
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
