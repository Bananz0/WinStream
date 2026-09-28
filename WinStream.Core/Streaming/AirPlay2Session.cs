#nullable enable

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Core.Streaming;

/// <summary>AirPlay 2 sender: HKP, encrypted RTSP, event keep-alive, realtime ALAC RTP.</summary>
public sealed class AirPlay2Session : IAirPlaySession
{
    private const int FramesPerPacket = AlacEncoder.FramesPerPacket;

    /// <summary>
    /// Default classic RAOP sync latency (~2 s at 44.1 kHz) until the orchestrator
    /// applies a responsiveness preset / Auto step.
    /// </summary>
    private const uint DefaultLatencyFrames = 88200;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(1);

    /// <summary>Receivers announce at 1 s; three intervals is a generous lock window.</summary>
    private static readonly TimeSpan PtpLockTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Receivers drop the session after ~30 s without a sender heartbeat.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    private readonly DeviceInfo _receiver;
    private readonly string _senderDeviceId;
    private readonly PairingOptions? _pairingOptions;
    private readonly SessionStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly PcmPacketBuffer _pcmBuffer = new();
    private readonly List<byte[]> _submitPackets = new();
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
    private uint _latencyFrames = DefaultLatencyFrames;
    private byte[]? _lastSentPcm;
    private int _pendingLowerSteps;
    private long _ptpReseedCount;
    private bool _disposed;

    /// <param name="senderDeviceId">
    /// Per-install sender MAC, resolved and persisted by the app. Sessions never touch
    /// settings themselves; an unsupplied ID falls back to a throwaway per-session value.
    /// </param>
    /// <param name="pairingOptions">
    /// Optional persistent HomeKit pairing. When omitted, connections use transient
    /// pair-setup (macOS will prompt Accept every time).
    /// </param>
    public AirPlay2Session(
        DeviceInfo receiver,
        string? senderDeviceId = null,
        PairingOptions? pairingOptions = null)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _senderDeviceId = SenderIdentity.LooksLikeMac(senderDeviceId)
            ? senderDeviceId!
            : SenderIdentity.CreateLocallyAdministeredMac();
        _pairingOptions = pairingOptions;
        ReceiverId = ReceiverKey.For(receiver);
        _stateMachine.StateChanged += (_, change) =>
            StateChanged?.Invoke(this, change);
    }

    public event EventHandler<SessionStateChanged>? StateChanged;

    public string ReceiverId { get; }

    public uint EffectiveLatencyFrames => Volatile.Read(ref _latencyFrames);

    /// <summary>
    /// How many times this session's <see cref="PtpClock"/> has re-seeded (a clock
    /// jump, not a smooth correction). Callers such as the Auto latency controller
    /// poll the delta of this counter across a signal window and feed it in as a
    /// re-anchor/pressure signal, rather than the session applying a re-seed silently.
    /// </summary>
    public long PtpReseedCount => Interlocked.Read(ref _ptpReseedCount);

    /// <summary>
    /// Updates the sync/announce latency, expressed only as which RTP numbers are
    /// emitted when — never as a bare re-mapping of the announced anchor.
    /// </summary>
    /// <remarks>
    /// A raise bursts one filler packet (a repeat of the last real packet's PCM, or
    /// silence when none has been sent yet) per packet of latency being added, while
    /// atomically advancing the RTP head by the same amount — so the announced
    /// <c>head - latency</c> pairing never moves even though the receiver's buffer
    /// grows. A lower is opportunistic: it only takes effect the next time a
    /// genuinely silent outgoing packet would have been sent, dropping that one
    /// packet (never advancing the RTP head for it) while shrinking latency by the
    /// same amount, so the pairing again never moves. Before the session is actually
    /// streaming (no bound audio socket yet) there is no receiver-visible anchor to
    /// preserve, so the field is written directly.
    /// </remarks>
    public void SetEffectiveLatencyFrames(uint frames)
    {
        var target = LatencyAutoController.ClampEffectiveFrames(frames);
        List<(ushort Sequence, uint Timestamp, byte[] Pcm)>? fillerBurst = null;
        lock (_mediaGate)
        {
            var current = _latencyFrames;
            if (target == current)
            {
                return;
            }

            if (!IsStreamingLocked())
            {
                _latencyFrames = target;
                return;
            }

            if (target > current)
            {
                fillerBurst = PrepareRaiseLocked(current, target);
            }
            else
            {
                RequestLowerLatencyLocked(current, target);
            }
        }

        // Encode+encrypt+send happens outside _mediaGate, matching EmitAudioPacket's
        // established pattern: state (sequence/timestamp/latency) is advanced
        // atomically under the lock above, but the actual I/O for a burst that can
        // be many hundreds of packets on a large raise must not block the real-time
        // audio thread's own SubmitPcm calls for its duration.
        if (fillerBurst is not null)
        {
            foreach (var (sequence, timestamp, pcm) in fillerBurst)
            {
                EncodeAndSend(pcm, sequence, timestamp, marker: false);
            }
        }
    }

    /// <summary>Caller must hold <see cref="_mediaGate"/>.</summary>
    private bool IsStreamingLocked() =>
        State == SessionState.Streaming &&
        _audioSocket is not null &&
        _audioEndpoint is not null &&
        _shk is not null;

    /// <summary>
    /// Caller must hold <see cref="_mediaGate"/>. Advances the RTP head, sequence
    /// numbers, and latency field for one filler packet per packet interval of
    /// latency being added — by the exact same amount on every step, so the
    /// announced anchor (<c>head - latency</c>) is invariant across the whole raise
    /// regardless of how many filler packets it takes — and returns the
    /// (sequence, timestamp, pcm) tuples to encode and send. The caller sends them
    /// after releasing the lock; this method only advances state.
    /// </summary>
    private List<(ushort Sequence, uint Timestamp, byte[] Pcm)> PrepareRaiseLocked(
        uint current,
        uint target)
    {
        long deltaFrames = target - current;
        var packets = (int)Math.Max(1, (deltaFrames + FramesPerPacket - 1) / FramesPerPacket);
        var burst = new List<(ushort, uint, byte[])>(packets);

        for (var i = 0; i < packets; i++)
        {
            var filler = _lastSentPcm ?? new byte[AlacEncoder.PcmBytesPerPacket];
            burst.Add((_sequenceNumber++, _rtpTimestamp, filler));
            _rtpTimestamp += (uint)FramesPerPacket;
        }

        _latencyFrames = current + (uint)(packets * FramesPerPacket);
        return burst;
    }

    /// <summary>
    /// Caller must hold <see cref="_mediaGate"/>. A lower cannot burst anything —
    /// there is no safe "extra" audio to remove immediately without a jump — so it
    /// is recorded as a pending step and only realized in <see cref="SubmitPcm"/>
    /// the next time a genuinely silent outgoing packet is about to be sent
    /// (already gated upstream on a detected silence window by the Auto latency
    /// controller before this method is ever called).
    /// </summary>
    private void RequestLowerLatencyLocked(uint current, uint target)
    {
        long deltaFrames = current - target;
        var packets = (int)Math.Max(1, (deltaFrames + FramesPerPacket - 1) / FramesPerPacket);
        _pendingLowerSteps += packets;
    }

    public void SetAudioFidelity(AudioFidelity fidelity) =>
        _pcmBuffer.Fidelity = fidelity;

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
                    DeviceId = _senderDeviceId
                };
                _control = client;

                await client.ConnectAndPairAsync(_pairingOptions, cancellationToken)
                    .ConfigureAwait(false);
                _shk = client.Keys.AudioSharedKey();
                await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);

                // START_PLAYBACK order for PTP devices:
                // session SETUP → event channel → RECORD → PTP start → stream SETUP.
                _ptp = new PtpClock(PtpClock.ClockIdFromDeviceId(client.DeviceId));
                _ptp.Reseeded += OnPtpReseeded;
                _ptp.Bind();

                await client.SessionSetupAsync(cancellationToken).ConfigureAwait(false);

                _events = new EventChannel();
                _events.Faulted += OnEventChannelFaulted;
                await _events.ConnectAsync(
                    _receiver.IPAddress,
                    client.EventPort,
                    client.Keys.EventsWriteKey.ToArray(),
                    client.Keys.EventsReadKey.ToArray(),
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

                var (setupMin, setupMax) = (
                    LatencyAutoController.SetupLatencyMin(EffectiveLatencyFrames),
                    LatencyAutoController.SetupLatencyMax(EffectiveLatencyFrames));
                await client.StreamSetupAsync(
                    GetLocalPort(_controlSocket),
                    _shk,
                    setupMin,
                    setupMax,
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
                lock (_mediaGate)
                {
                    _lastSentPcm = null;
                    _pendingLowerSteps = 0;
                }

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

            _submitPackets.Clear();
            _pcmBuffer.Push(pcm.Span, format, _submitPackets);
        }

        if (froze)
        {
            // Anchor-before-audio: the receiver drops every audio packet until it has
            // an RTP-to-PTP mapping, so the first announce must land synchronously
            // here, before any audio leaves this call — not via the background sync
            // task, which would otherwise race the foreach loop below.
            SendAnchorAnnouncementSync(first: true);
            _rtpBaseFrozen?.TrySetResult();
        }

        foreach (var packetPcm in _submitPackets)
        {
            EmitAudioPacket(packetPcm);
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

    /// <summary>
    /// Test seam: binds the send-path fields a real <see cref="ConnectAsync"/> would
    /// have set, without a live AirPlay handshake, so <see cref="SetEffectiveLatencyFrames"/>
    /// and <see cref="SubmitPcm"/> exercise their real atomic raise/lower/drop paths
    /// against a loopback socket instead of taking the "not yet streaming" bypass.
    /// </summary>
    internal void SeedNetworkForTests(
        UdpClient audioSocket,
        IPEndPoint audioEndpoint,
        byte[] sharedKey)
    {
        _audioSocket = audioSocket;
        _audioEndpoint = audioEndpoint;
        _shk = sharedKey;
        _ssrc = 1;
    }

    /// <summary>Test seam: raises <see cref="PtpClock.Reseeded"/> without a live PTP peer.</summary>
    internal void RaisePtpReseededForTests(PtpReseedEventArgs args) =>
        OnPtpReseeded(this, args);

    /// <summary>Test seam: number of one-packet lower steps still awaiting a silent packet.</summary>
    internal int PendingLowerStepsForTests => Volatile.Read(ref _pendingLowerSteps);

    /// <summary>
    /// Emits one real (source-derived) packet, or silently drops it in place of a
    /// pending opportunistic latency lower — see <see cref="RequestLowerLatencyLocked"/>.
    /// A drop advances neither the RTP head nor the sequence number for this packet,
    /// while shrinking the latency field by the same one-packet amount, so the
    /// announced <c>head - latency</c> pairing does not move.
    /// </summary>
    private void EmitAudioPacket(byte[] pcmPacket)
    {
        if (_audioSocket is null || _audioEndpoint is null || _shk is null)
        {
            return;
        }

        ushort sequence;
        uint timestamp;
        bool marker;
        lock (_mediaGate)
        {
            if (_pendingLowerSteps > 0 && IsSilentPacket(pcmPacket))
            {
                _pendingLowerSteps--;
                _latencyFrames = Math.Max(
                    _latencyFrames - (uint)FramesPerPacket,
                    LatencyAutoController.PacketFloorFrames);
                return;
            }

            sequence = _sequenceNumber++;
            timestamp = _rtpTimestamp;
            _rtpTimestamp += (uint)FramesPerPacket;
            marker = _sendMarker;
            _sendMarker = false;
            _lastSentPcm = pcmPacket;
        }

        EncodeAndSend(pcmPacket, sequence, timestamp, marker);
    }

    private void EncodeAndSend(byte[] pcmPacket, ushort sequence, uint timestamp, bool marker)
    {
        if (_audioSocket is null || _audioEndpoint is null || _shk is null)
        {
            return;
        }

        Span<byte> alac = stackalloc byte[AlacEncoder.GetMaxEncodedLength(pcmPacket.Length)];
        var alacLength = AlacEncoder.Encode(pcmPacket, alac);

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

    private static bool IsSilentPacket(byte[] pcmPacket)
    {
        foreach (var b in pcmPacket)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every re-seed is a clock jump, not a smooth correction, so it must be
    /// observable rather than silently reflected into the next announcement. This
    /// only counts the event for <see cref="PtpReseedCount"/>; the Auto latency
    /// controller (fed via the orchestrator's windowed signals) decides whether and
    /// how much to raise, then calls <see cref="SetEffectiveLatencyFrames"/> as usual.
    /// </summary>
    private void OnPtpReseeded(object? sender, PtpReseedEventArgs e)
    {
        Interlocked.Increment(ref _ptpReseedCount);
        AppLog.Warn(
            "ap2",
            $"PTP re-seed observed old={e.PreviousOffsetNanoseconds / 1_000_000.0:F3} ms " +
            $"new={e.NewOffsetNanoseconds / 1_000_000.0:F3} ms master=0x{e.MasterClockId:X16}");
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

            await frozen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await RunAnchorLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the session is torn down or fails before audio starts.
        }
    }

    /// <summary>
    /// Synchronous counterpart to <see cref="RunAnchorLoopAsync"/>'s per-tick send,
    /// used once to guarantee the very first anchor announcement reaches the wire
    /// before any audio packet does (see the freeze handling in
    /// <see cref="SubmitPcm"/>). Reads <c>_rtpTimestamp</c>/<c>_latencyFrames</c>
    /// under the same lock the anchor loop uses, and marks <c>_firstSync</c>
    /// consumed so the background loop's own next tick does not re-announce as
    /// "first" — a benign, idempotent repeat of the same mapping shortly after is
    /// harmless either way.
    /// </summary>
    private void SendAnchorAnnouncementSync(bool first)
    {
        var socket = _controlSocket;
        var endpoint = _controlEndpoint;
        var clock = _ptp;
        if (socket is null || endpoint is null || clock is null)
        {
            return;
        }

        var clockId = clock.MasterClockId;
        if (clockId == 0)
        {
            AppLog.Warn("ap2", "Skipping first time announce — PTP grandmaster unknown.");
            return;
        }

        uint now;
        uint latency;
        lock (_mediaGate)
        {
            now = _rtpTimestamp;
            latency = _latencyFrames;
            _firstSync = false;
        }

        var packet = new byte[28];
        var length = RtpPacketizer.WriteTimeAnnouncePacket(
            packet,
            now - latency,
            clock.NowNanoseconds,
            now,
            clockId,
            first);

        try
        {
            _ = socket.Send(packet.AsSpan(0, length), endpoint);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The periodic anchor loop will retry on its next tick.
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
                uint latency;
                bool first;
                lock (_mediaGate)
                {
                    now = _rtpTimestamp;
                    latency = _latencyFrames;
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
                    now - latency,
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
            _ptp.Reseeded -= OnPtpReseeded;
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

}
