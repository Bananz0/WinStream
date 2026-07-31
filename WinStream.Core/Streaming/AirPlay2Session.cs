#nullable enable

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Protocol.Raop;
using WinStream.Network;
using WinStream.Networking;

namespace WinStream.Core.Streaming;

/// <summary>AirPlay 2 sender: HKP, encrypted RTSP, event keep-alive, realtime ALAC RTP.</summary>
public sealed class AirPlay2Session : IAirPlaySession
{
    private const int FramesPerPacket = AlacEncoder.FramesPerPacket;

    /// <summary>
    /// Classic RAOP sync latency (~2 s at 44.1 kHz). Matches OwnTone / pyatv /
    /// akustikrausch sync packets (latencyMax), not latencyMin.
    /// </summary>
    private const uint LatencyFrames = 88200;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(1);

    /// <summary>Receivers announce at 1 s; three intervals is a generous lock window.</summary>
    private static readonly TimeSpan PtpLockTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Receivers drop the session after ~30 s without a sender heartbeat.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    private readonly DeviceInfo _receiver;
    private readonly SessionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly PcmPacketBuffer _pcmBuffer = new();
    private readonly object _mediaGate = new();
    private EncryptedRtspClient? _control;
    private EventChannel? _events;
    private PtpClock? _ptp;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAlive;
    private CancellationTokenSource? _syncCts;
    private Task? _syncLoop;
    private bool _firstSync = true;
    private UdpClient? _audioSocket;
    private UdpClient? _controlSocket;
    private IPEndPoint? _audioEndpoint;
    private IPEndPoint? _controlEndpoint;
    private byte[]? _shk;
    private ushort _sequenceNumber;
    private uint _rtpTimestamp;
    private uint _ssrc;
    private bool _sendMarker = true;
    private bool _rtpBasePending = true;
    private TaskCompletionSource? _rtpBaseFrozen;
    private float _volumeDb = -20f;
    private bool _disposed;

    public AirPlay2Session(DeviceInfo receiver)
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

            try
            {
                var address = IPAddress.Parse(NormalizeHost(_receiver.IPAddress));
                _controlSocket = BindUdp(address.AddressFamily);
                _audioSocket = BindUdp(address.AddressFamily);
                UdpSocketConfigurer.SuppressUdpConnReset(_controlSocket);
                UdpSocketConfigurer.SuppressUdpConnReset(_audioSocket);

                var client = new EncryptedRtspClient(_receiver.IPAddress, _receiver.Port)
                {
                    DeviceId = ResolveSenderDeviceId()
                };
                _control = client;

                await client.ConnectAndPairAsync(cancellationToken).ConfigureAwait(false);
                _shk = client.Pairing.AudioSharedKey();
                await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);

                // START_PLAYBACK order for PTP devices:
                // session SETUP → event channel → RECORD → PTP start → stream SETUP.
                _ptp = new PtpClock(PtpClock.ClockIdFromDeviceId(client.DeviceId));
                _ptp.Bind();

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

                // No SETPEERS: a bare address list replaces the SETUP peer with
                // one that has no ClockID or ClockPorts, and the receiver then
                // stops talking PTP to us entirely.
                _ptp.Start(address);
                if (!await _ptp.WaitForLockAsync(PtpLockTimeout, cancellationToken)
                        .ConfigureAwait(false) ||
                    _ptp.MasterClockId == 0)
                {
                    throw new InvalidOperationException(
                        "PTP lock failed — cannot announce a timeline without the " +
                        "receiver's grandmaster clock.");
                }

                await client.StreamSetupAsync(
                    GetLocalPort(_controlSocket),
                    _shk,
                    cancellationToken).ConfigureAwait(false);

                _audioEndpoint = new IPEndPoint(address, client.DataPort);
                _controlEndpoint = client.ControlPort > 0
                    ? new IPEndPoint(address, client.ControlPort)
                    : null;
                _sequenceNumber = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue);
                _rtpTimestamp = (uint)RandomNumberGenerator.GetInt32(0, int.MaxValue);
                _ssrc = (uint)client.SessionUuid.GetHashCode(StringComparison.Ordinal);
                if (_ssrc == 0)
                {
                    _ssrc = 1;
                }

                _sendMarker = true;
                _rtpBasePending = true;
                _rtpBaseFrozen = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pcmBuffer.Reset();

                await client.SetVolumeAsync(_volumeDb, cancellationToken).ConfigureAwait(false);
                StartSyncLoop();
                StartKeepAlive(client);
                AppLog.Info(
                    "ap2",
                    $"Streaming eventPort={client.EventPort} dataPort={client.DataPort} " +
                    $"controlPort={client.ControlPort} " +
                    $"ptpMaster=0x{_ptp.MasterClockId:X16} " +
                    $"setupClock=0x{client.RemoteClockId ?? 0:X16}");
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
        bool froze;
        lock (_mediaGate)
        {
            // Adopt the fan-out clock once, then advance with the packets we
            // actually emit. Restamping on every submit desynchronises the RTP
            // clock from the audio, because a submit chunk and an ALAC packet
            // hold different frame counts — the receiver then sees timestamps
            // jump backwards and discards the stream as late.
            froze = SharedMediaClockAlignment.Freeze(
                ref _rtpTimestamp,
                ref _rtpBasePending,
                sharedMediaTimestamp);

            packets = new System.Collections.Generic.List<byte[]>(
                _pcmBuffer.Push(pcm.Span, format)).ToArray();
        }

        if (froze)
        {
            _rtpBaseFrozen?.TrySetResult();
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
            await StopKeepAliveAsync().ConfigureAwait(false);
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

    private void StartKeepAlive(EncryptedRtspClient client)
    {
        _keepAliveCts = new CancellationTokenSource();
        var token = _keepAliveCts.Token;
        _keepAlive = Task.Run(() => KeepAliveLoopAsync(client, token), CancellationToken.None);
    }

    private async Task KeepAliveLoopAsync(
        EncryptedRtspClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await client.SendFeedbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on teardown.
        }
        catch (Exception ex)
        {
            AppLog.Warn("ap2", $"Keep-alive fault: {ex.GetType().Name}: {ex.Message}");
            TransitionStreamingToFailed("AirPlay keep-alive failed.");
        }
    }

    private async Task StopKeepAliveAsync()
    {
        if (_keepAliveCts is not null)
        {
            await _keepAliveCts.CancelAsync().ConfigureAwait(false);
        }

        if (_keepAlive is not null)
        {
            try
            {
                await _keepAlive.ConfigureAwait(false);
            }
            catch
            {
                // Faults already logged by the loop.
            }

            _keepAlive = null;
        }

        _keepAliveCts?.Dispose();
        _keepAliveCts = null;
    }

    private void OnEventChannelFaulted(object? sender, Exception ex)
    {
        // The receiver closes this channel during normal teardown, so only an
        // unexpected close is worth warning about.
        if (State != SessionState.Streaming)
        {
            return;
        }

        AppLog.Warn("ap2", $"Event channel fault: {ex.GetType().Name}: {ex.Message}");
        TransitionStreamingToFailed("AirPlay event channel closed.");
    }

    private void TransitionStreamingToFailed(string reason)
    {
        if (State != SessionState.Streaming)
        {
            return;
        }

        _stateMachine.TransitionTo(SessionState.Failed, reason);
        // If audio never started, release the timeline barrier immediately.
        // The sync task then exits instead of remaining parked until disposal.
        _rtpBaseFrozen?.TrySetCanceled();
    }

    /// <summary>Test seam: Streaming + pending freeze barrier without a full handshake.</summary>
    internal Task SeedStreamingFreezeBarrierForTests()
    {
        if (State == SessionState.Disconnected)
        {
            _stateMachine.TransitionTo(SessionState.Connecting);
            _stateMachine.TransitionTo(SessionState.Streaming);
        }

        _rtpBaseFrozen ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _rtpBaseFrozen.Task;
    }

    internal void FailStreamingForTests(string reason) =>
        TransitionStreamingToFailed(reason);

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
            _ = _audioSocket.Send(packet[..length], _audioEndpoint);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Transient UDP send failures ignored; reconnect handled elsewhere.
        }
    }

    /// <summary>
    /// Publishes the RTP-to-PTP mapping on the control channel. A realtime
    /// receiver drops every audio packet until these arrive: it has no other way
    /// to turn an RTP timestamp into a render deadline.
    /// </summary>
    private void StartSyncLoop()
    {
        if (_controlEndpoint is null)
        {
            AppLog.Warn("ap2", "Receiver advertised no control port; skipping sync loop.");
            return;
        }

        _firstSync = true;
        _syncCts = new CancellationTokenSource();
        var token = _syncCts.Token;
        _syncLoop = Task.Run(() => RunSyncLoopAsync(token), token);
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        // The anchor maps an RTP timestamp onto PTP time, so it can only be sent
        // once the timebase the audio will actually use is settled. Announcing
        // first and letting the fan-out clock rebase afterwards leaves the
        // receiver decoding against a timeline the stream never emits, and it
        // renders nothing.
        var frozen = _rtpBaseFrozen;
        try
        {
            if (frozen is null)
            {
                await RunAnchorLoopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await TimelineAnchorGate.RunAfterFreezeAsync(
                    frozen.Task,
                    RunAnchorLoopAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is torn down or fails before audio starts.
        }
    }

    private async Task RunAnchorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SyncInterval);
        do
        {
            var socket = _controlSocket;
            var endpoint = _controlEndpoint;
            var clock = _ptp;
            if (socket is null || endpoint is null || clock is null)
            {
                return;
            }

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

                var clockId = clock.MasterClockId;
                if (clockId == 0)
                {
                    AppLog.Warn("ap2", "Skipping time announce — PTP grandmaster unknown.");
                    continue;
                }

                var packet = new byte[28];
                var length = RtpPacketizer.WriteTimeAnnouncePacket(
                    packet,
                    now - LatencyFrames,
                    clock.NowNanoseconds,
                    now,
                    clockId,
                    first);
                await socket.SendAsync(packet.AsMemory(0, length), endpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Keep syncing for as long as the session lives.
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task StopSyncLoopAsync()
    {
        if (_syncCts is null)
        {
            return;
        }

        await _syncCts.CancelAsync().ConfigureAwait(false);
        if (_syncLoop is not null)
        {
            try
            {
                await _syncLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        _syncCts.Dispose();
        _syncCts = null;
        _syncLoop = null;
    }

    private async Task ReleaseResourcesAsync()
    {
        await StopKeepAliveAsync().ConfigureAwait(false);
        await StopSyncLoopAsync().ConfigureAwait(false);

        if (_ptp is not null)
        {
            await _ptp.DisposeAsync().ConfigureAwait(false);
            _ptp = null;
        }

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
        _controlEndpoint = null;
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

    /// <summary>
    /// Per-install locally administered MAC. Shared hard-coded IDs collide when
    /// two WinStream instances share a LAN.
    /// </summary>
    private static string ResolveSenderDeviceId()
    {
        var store = new SettingsStore();
        var settings = store.Load();
        if (!string.IsNullOrWhiteSpace(settings.SenderDeviceId) &&
            LooksLikeMac(settings.SenderDeviceId))
        {
            return settings.SenderDeviceId;
        }

        var bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)((bytes[0] | 0x02) & 0xFE); // locally administered, unicast
        var id = string.Create(
            17,
            bytes,
            static (span, src) =>
            {
                const string hex = "0123456789ABCDEF";
                var o = 0;
                for (var i = 0; i < 6; i++)
                {
                    if (i > 0)
                    {
                        span[o++] = ':';
                    }

                    span[o++] = hex[src[i] >> 4];
                    span[o++] = hex[src[i] & 0xF];
                }
            });
        settings.SenderDeviceId = id;
        store.Save(settings);
        return id;
    }

    private static bool LooksLikeMac(string value)
    {
        var hex = value.Replace(":", string.Empty).Replace("-", string.Empty);
        return hex.Length == 12 &&
               ulong.TryParse(
                   hex,
                   System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out _);
    }
}
