#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Protocol.Raop;
using WinStream.Core.Streaming;
using WinStream.Network;
using WinStream.Networking;

namespace WinStream.Streaming;

/// <summary>AirPlay 2 sender: HKP, encrypted RTSP, event keep-alive, realtime ALAC RTP.</summary>
public sealed class AirPlay2Session : IAirPlaySession
{
    private const int FramesPerPacket = AlacEncoder.FramesPerPacket;

    private readonly DeviceInfo _receiver;
    private readonly bool _gateEnabled;
    private readonly SessionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly PcmPacketBuffer _pcmBuffer = new();
    private readonly object _mediaGate = new();
    private EncryptedRtspClient? _control;
    private EventChannel? _events;
    private UdpClient? _audioSocket;
    private UdpClient? _controlSocket;
    private IPEndPoint? _audioEndpoint;
    private byte[]? _shk;
    private ushort _sequenceNumber;
    private uint _rtpTimestamp;
    private uint _ssrc;
    private bool _sendMarker = true;
    private float _volumeDb = -20f;
    private bool _disposed;

    public AirPlay2Session(DeviceInfo receiver, bool gateEnabled)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _gateEnabled = gateEnabled;
        ReceiverId = !string.IsNullOrWhiteSpace(receiver.DeviceID)
            ? receiver.DeviceID
            : $"{receiver.IPAddress}:{receiver.Port}";
        _stateMachine.StateChanged += (_, change) =>
            StateChanged?.Invoke(this, change);
    }

    public event EventHandler<SessionStateChanged>? StateChanged;

    public string ReceiverId { get; }

    public SessionState State => _stateMachine.State;

    public int EventPort => _control?.EventPort ?? 0;

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

            _stateMachine.TransitionTo(SessionState.Connecting);
            if (!_gateEnabled)
            {
                _stateMachine.TransitionTo(
                    SessionState.Failed,
                    "AirPlay 2 is disabled. Enable the experimental gate in settings after AP2 validation.");
                throw new InvalidOperationException(
                    "AirPlay 2 streaming is capability-gated and currently disabled.");
            }

            try
            {
                var address = IPAddress.Parse(NormalizeHost(_receiver.IPAddress));
                _controlSocket = BindUdp(address.AddressFamily);
                _audioSocket = BindUdp(address.AddressFamily);
                UdpSocketConfigurer.SuppressUdpConnReset(_controlSocket);
                UdpSocketConfigurer.SuppressUdpConnReset(_audioSocket);

                var client = new EncryptedRtspClient(_receiver.IPAddress, _receiver.Port)
                {
                    DeviceId = ResolveDeviceId(_receiver)
                };
                _control = client;

                await client.ConnectAndPairAsync(cancellationToken).ConfigureAwait(false);
                _shk = client.Pairing.AudioSharedKey();
                await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
                await client.SessionSetupAsync(cancellationToken).ConfigureAwait(false);

                _events = new EventChannel();
                _events.Faulted += OnEventChannelFaulted;
                await _events.ConnectAsync(
                    _receiver.IPAddress,
                    client.EventPort,
                    client.Pairing.EventsWriteKey.ToArray(),
                    client.Pairing.EventsReadKey.ToArray(),
                    cancellationToken).ConfigureAwait(false);

                await client.RecordAsync(cancellationToken).ConfigureAwait(false);
                await client.StreamSetupAsync(
                    GetLocalPort(_controlSocket),
                    _shk,
                    cancellationToken).ConfigureAwait(false);

                _audioEndpoint = new IPEndPoint(address, client.DataPort);
                _sequenceNumber = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue);
                _rtpTimestamp = (uint)RandomNumberGenerator.GetInt32(0, int.MaxValue);
                _ssrc = (uint)client.SessionUuid.GetHashCode(StringComparison.Ordinal);
                if (_ssrc == 0)
                {
                    _ssrc = 1;
                }

                _sendMarker = true;
                _pcmBuffer.Reset();
                await client.SetVolumeAsync(_volumeDb, cancellationToken).ConfigureAwait(false);
                AppLog.Info(
                    "ap2",
                    $"Streaming eventPort={client.EventPort} dataPort={client.DataPort} " +
                    $"controlPort={client.ControlPort}");
                _stateMachine.TransitionTo(SessionState.Streaming);
            }
            catch (Exception ex)
            {
                await ReleaseResourcesAsync().ConfigureAwait(false);
                _stateMachine.TransitionTo(SessionState.Failed, ex.Message);
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
            _audioEndpoint is null ||
            _shk is null)
        {
            return;
        }

        byte[][] packets;
        lock (_mediaGate)
        {
            if (sharedMediaTimestamp.HasValue)
            {
                _rtpTimestamp = sharedMediaTimestamp.Value;
            }

            packets = new System.Collections.Generic.List<byte[]>(
                _pcmBuffer.Push(pcm.Span, format)).ToArray();
        }

        foreach (var packetPcm in packets)
        {
            SendAudioPacket(packetPcm);
        }
    }

    public async Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volumeDb = Math.Clamp(volumeDb, -144f, 0f);
        if (State != SessionState.Streaming || _control is null)
        {
            return;
        }

        await _control.SetVolumeAsync(_volumeDb, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is SessionState.Disconnected)
            {
                return;
            }

            _stateMachine.TransitionTo(SessionState.Disconnecting);
            if (_control is not null)
            {
                try
                {
                    await _control.TeardownAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort.
                }
            }

            await ReleaseResourcesAsync().ConfigureAwait(false);
            _stateMachine.TransitionTo(SessionState.Disconnected);
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

    private void OnEventChannelFaulted(object? sender, Exception ex)
    {
        AppLog.Warn("ap2", $"Event channel fault: {ex.GetType().Name}");
        if (State == SessionState.Streaming)
        {
            _stateMachine.TransitionTo(SessionState.Failed, "AirPlay event channel closed.");
        }
    }

    private void SendAudioPacket(byte[] pcmPacket)
    {
        if (_audioSocket is null || _audioEndpoint is null || _shk is null)
        {
            return;
        }

        Span<byte> alac = stackalloc byte[AlacEncoder.GetMaxEncodedLength(pcmPacket.Length)];
        var alacLength = AlacEncoder.Encode(pcmPacket, alac);

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

        var encrypted = RtpChaChaEncryptor.EncryptPayload(
            _shk,
            sequence,
            timestamp,
            _ssrc,
            alac[..alacLength]);

        Span<byte> packet = stackalloc byte[12 + encrypted.Length];
        var length = RtpPacketizer.WriteAudioPacket(
            packet,
            sequence,
            timestamp,
            _ssrc,
            encrypted,
            marker);

        try
        {
            _ = _audioSocket.SendAsync(packet[..length].ToArray(), _audioEndpoint);
        }
        catch
        {
            // Transient UDP send failures ignored; reconnect handled elsewhere.
        }
    }

    private async Task ReleaseResourcesAsync()
    {
        if (_events is not null)
        {
            _events.Faulted -= OnEventChannelFaulted;
            await _events.DisposeAsync().ConfigureAwait(false);
            _events = null;
        }

        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
            _control = null;
        }

        _audioSocket?.Dispose();
        _controlSocket?.Dispose();
        _audioSocket = null;
        _controlSocket = null;
        _audioEndpoint = null;
        if (_shk is not null)
        {
            CryptographicOperations.ZeroMemory(_shk);
            _shk = null;
        }
    }

    private static UdpClient BindUdp(AddressFamily family)
    {
        var client = new UdpClient(0, family);
        return client;
    }

    private static int GetLocalPort(UdpClient client) =>
        ((IPEndPoint)client.Client.LocalEndPoint!).Port;

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']');

    private static string ResolveDeviceId(DeviceInfo receiver)
    {
        if (!string.IsNullOrWhiteSpace(receiver.DeviceID) &&
            receiver.DeviceID.Contains(':', StringComparison.Ordinal))
        {
            return receiver.DeviceID;
        }

        return "02:00:00:00:00:01";
    }
}
