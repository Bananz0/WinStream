using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinStream.Core.Logging;
using WinStream.Core.Network;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// IEEE-1588 slave that tracks the receiver's grandmaster clock.
/// </summary>
/// <remarks>
/// macOS receivers keep mastership of the PTP domain and refuse to slave to the
/// sender, so the sender has to read their timeline and stamp the RTP anchor in
/// it. Announcing as grandmaster instead makes the receiver report
/// "Grandmaster ID mismatch" and buffer nothing.
/// </remarks>
public sealed class PtpClock : IAsyncDisposable
{
    public const int EventPort = 319;
    public const int GeneralPort = 320;

    /// <summary>
    /// Our PTP port identity. The receiver advertises the same value per peer in
    /// <c>ClockPorts</c> and will not enable a peer whose port it cannot match.
    /// </summary>
    public const ushort PortNumber = 0x8005;

    private const int HeaderLength = 34;
    private const byte PtpVersion = 2;
    private const byte TransportSpecific = 0x10; // expected by Apple / nqptp

    private const byte MessageSync = 0x00;
    private const byte MessageFollowUp = 0x08;
    private const byte MessageAnnounce = 0x0B;

    private const ushort FlagTwoStep = 1 << 9;

    /// <summary>EMA weight toward each accepted sample (plan: α = 0.2).</summary>
    private const double EmaAlpha = 0.2;

    /// <summary>Reject Follow_Up offsets that jump more than this vs the EMA.</summary>
    private const long SpikeRejectNanoseconds = 50_000_000; // 50 ms

    /// <summary>Samples must agree within this much to be treated as one clock.</summary>
    private const long AgreementToleranceNanoseconds = 5_000_000; // 5 ms

    /// <summary>Mutually-agreeing samples required from one peer before we lock.</summary>
    private const int LockSampleCount = 3;

    /// <summary>Mutually-agreeing rejections from the locked peer before we re-seed.</summary>
    private const int ReseedSampleCount = 8;

    /// <summary>Bounds memory if many stray sources are heard before we lock.</summary>
    private const int MaxTrackedPreLockPeers = 8;

    private static readonly TimeSpan ReceiveFaultBackoff = TimeSpan.FromMilliseconds(50);

    private readonly ulong _clockId;
    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly object _lockGate = new();
    private readonly LogRateLimiter _deltaLog = new(TimeSpan.FromSeconds(1));
    private readonly Dictionary<IPAddress, List<long>> _pendingLockSamples = new();
    private readonly List<(long Offset, ulong ClockId)> _pendingReseedSamples = new();
    private UdpClient? _event;
    private UdpClient? _general;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private IPEndPoint? _peerEvent;
    private TaskCompletionSource? _lock;
    private long _syncArrivalNs;
    private ushort _syncSequence;
    private bool _syncPending;
    private IPAddress? _syncSourceAddress;
    private ulong _syncClockId;
    private long _offsetNs;
    private ulong _masterClockId;
    private IPAddress? _lockedPeerAddress;
    private int _samples;
    private int _spikesRejected;
    private int _consecutiveReceiveFaults;
    private bool _disposed;

    public PtpClock(ulong clockId)
    {
        _clockId = clockId == 0 ? 1UL : clockId;
    }

    public ulong ClockId => _clockId;

    /// <summary>Clock identity of the grandmaster we are tracking, 0 until heard.</summary>
    public ulong MasterClockId => Volatile.Read(ref _masterClockId);

    /// <summary>Address of the locked peer, once <see cref="LockSampleCount"/> samples agree.</summary>
    public IPAddress? LockedPeerAddress => Volatile.Read(ref _lockedPeerAddress);

    public bool IsLocked => Volatile.Read(ref _samples) > 0;

    /// <summary>
    /// Raised when the clock re-seeds after <see cref="ReseedSampleCount"/> consecutive,
    /// mutually-agreeing rejections from the locked peer. A re-seed is a clock jump, not
    /// a smooth correction, so callers (e.g. the Auto latency controller) must treat it as
    /// a re-anchor event rather than an ordinary sample.
    /// </summary>
    public event EventHandler<PtpReseedEventArgs>? Reseeded;

    /// <summary>Nanoseconds on the grandmaster's timeline.</summary>
    public ulong NowNanoseconds
    {
        get
        {
            var local = (long)_mono.Elapsed.TotalNanoseconds;
            var master = local + Interlocked.Read(ref _offsetNs);
            return master > 0 ? (ulong)master : 0UL;
        }
    }

    public static ulong ClockIdFromDeviceId(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var hex = deviceId.Replace(":", string.Empty).Replace("-", string.Empty);
        if (hex.Length != 12 || !ulong.TryParse(
                hex,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var mac))
        {
            mac = (ulong)Random.Shared.NextInt64(1, long.MaxValue) & 0xFFFFFFFFFFFF;
        }

        var upper = (mac >> 24) & 0xFFFFFF;
        var lower = mac & 0xFFFFFF;
        return (upper << 40) | (0xFFFEUL << 24) | lower;
    }

    public void Bind()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_event is not null)
        {
            return;
        }

        try
        {
            _event = BindPort(EventPort);
            _general = BindPort(GeneralPort);
        }
        catch (SocketException ex)
        {
            _event?.Dispose();
            _general?.Dispose();
            _event = null;
            _general = null;
            throw new InvalidOperationException(
                "Could not bind PTP ports 319/320. Close other PTP software " +
                "so AirPlay 2 can follow the receiver's clock.",
                ex);
        }
    }

    public void Start(IPAddress peer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(peer);
        if (_cts is not null)
        {
            return;
        }

        if (_event is null || _general is null)
        {
            Bind();
        }

        _peerEvent = new IPEndPoint(peer, EventPort);
        _lock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _receiveLoop = Task.Run(() => RunReceiveAsync(token), token);
        AppLog.Info("ptp", $"Slave started peer={peer} ports=319/320");
    }

    /// <summary>Resolves once an offset to the grandmaster has been measured.</summary>
    public async Task<bool> WaitForLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var pending = _lock;
        if (pending is null || IsLocked)
        {
            return IsLocked;
        }

        var completed = await Task.WhenAny(
                pending.Task,
                Task.Delay(timeout, cancellationToken))
            .ConfigureAwait(false);
        return completed == pending.Task && IsLocked;
    }

    /// <summary>Test seam: apply a captured PTP datagram as if it just arrived.</summary>
    internal void HandleIncomingForTests(
        byte[] packet,
        long arrivalNs,
        IPAddress? sourceAddress = null) =>
        HandleIncoming(packet, arrivalNs, sourceAddress);

    /// <summary>Test seam: set a synthetic offset without UDP.</summary>
    internal void SetOffsetForTests(long offsetNs, ulong masterClockId, IPAddress? peerAddress = null)
    {
        Interlocked.Exchange(ref _offsetNs, offsetNs);
        Volatile.Write(ref _masterClockId, masterClockId);
        Volatile.Write(ref _lockedPeerAddress, peerAddress);
        if (Interlocked.CompareExchange(ref _samples, 1, 0) == 0)
        {
            _lock?.TrySetResult();
        }
    }

    /// <summary>Test seam: apply a master/local pair through the production smoother.</summary>
    internal void ApplyOffsetForTests(
        long masterNs,
        long localNs,
        IPAddress? sourceAddress = null,
        ulong masterClockId = 0) =>
        ApplyOffset(masterNs, localNs, sourceAddress, masterClockId);

    /// <summary>How many Follow_Up samples were rejected as spikes.</summary>
    internal int SpikesRejectedForTests => Volatile.Read(ref _spikesRejected);

    /// <summary>Current smoothed offset (EMA state) for unit tests.</summary>
    internal long OffsetNanosecondsForTests => Interlocked.Read(ref _offsetNs);

    private static UdpClient BindPort(int port)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        UdpSocketConfigurer.SuppressUdpConnReset(socket);
        return socket;
    }

    private async Task RunReceiveAsync(CancellationToken cancellationToken)
    {
        if (_event is null || _general is null)
        {
            return;
        }

        var eventTask = ReceiveOneAsync(_event, cancellationToken);
        var generalTask = ReceiveOneAsync(_general, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var done = await Task.WhenAny(eventTask, generalTask).ConfigureAwait(false);
            UdpReceiveResult? result;
            if (done == eventTask)
            {
                result = await eventTask.ConfigureAwait(false);
                eventTask = ReceiveOneAsync(_event, cancellationToken);
            }
            else
            {
                result = await generalTask.ConfigureAwait(false);
                generalTask = ReceiveOneAsync(_general, cancellationToken);
            }

            if (result is { } datagram)
            {
                _consecutiveReceiveFaults = 0;
                HandleIncoming(
                    datagram.Buffer,
                    (long)_mono.Elapsed.TotalNanoseconds,
                    datagram.RemoteEndPoint.Address);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _consecutiveReceiveFaults++;
            if (_consecutiveReceiveFaults >= 8)
            {
                AppLog.Warn("ptp", "PTP receive loop exiting after repeated socket faults.");
                return;
            }

            try
            {
                await Task.Delay(ReceiveFaultBackoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<UdpReceiveResult?> ReceiveOneAsync(
        UdpClient socket,
        CancellationToken cancellationToken)
    {
        try
        {
            return await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void HandleIncoming(byte[] packet, long arrivalNs, IPAddress? sourceAddress)
    {
        if (packet.Length < HeaderLength)
        {
            return;
        }

        var messageType = (byte)(packet[0] & 0x0f);

        // Once locked, a stray PTP packet from another LAN device must not poison
        // the lock: only the peer we already tracked down is trusted. Packets
        // whose source we could not determine (e.g. the test seam) fall through.
        var lockedPeer = Volatile.Read(ref _lockedPeerAddress);
        if (lockedPeer is not null &&
            sourceAddress is not null &&
            !sourceAddress.Equals(lockedPeer) &&
            messageType is MessageAnnounce or MessageSync or MessageFollowUp)
        {
            return;
        }

        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(30));
        switch (messageType)
        {
            case MessageSync:
                HandleSync(packet, sequence, arrivalNs, sourceAddress);
                break;
            case MessageFollowUp when packet.Length >= HeaderLength + 10:
                HandleFollowUp(packet, sequence, sourceAddress);
                break;
        }
    }

    private void HandleSync(byte[] packet, ushort sequence, long arrivalNs, IPAddress? sourceAddress)
    {
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6));
        var clockId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(20));
        if ((flags & FlagTwoStep) != 0)
        {
            lock (_gate)
            {
                _syncArrivalNs = arrivalNs;
                _syncSequence = sequence;
                _syncPending = true;
                _syncSourceAddress = sourceAddress;
                _syncClockId = clockId;
            }

            return;
        }

        if (packet.Length >= HeaderLength + 10)
        {
            ApplyOffset(ReadTimestamp(packet.AsSpan(HeaderLength, 10)), arrivalNs, sourceAddress, clockId);
        }
    }

    private void HandleFollowUp(byte[] packet, ushort sequence, IPAddress? sourceAddress)
    {
        long arrival;
        IPAddress? syncAddress;
        ulong clockId;
        lock (_gate)
        {
            if (!_syncPending || _syncSequence != sequence)
            {
                return;
            }

            arrival = _syncArrivalNs;
            syncAddress = _syncSourceAddress;
            clockId = _syncClockId;
            _syncPending = false;
        }

        ApplyOffset(
            ReadTimestamp(packet.AsSpan(HeaderLength, 10)),
            arrival,
            sourceAddress ?? syncAddress,
            clockId);
    }

    private void ApplyOffset(long masterNs, long localNs, IPAddress? sourceAddress, ulong candidateClockId)
    {
        var offset = masterNs - localNs;
        var samples = Volatile.Read(ref _samples);
        if (samples == 0)
        {
            TryLock(offset, sourceAddress, candidateClockId);
            return;
        }

        var ema = Interlocked.Read(ref _offsetNs);
        var delta = Math.Abs(offset - ema);
        if (delta > SpikeRejectNanoseconds)
        {
            Interlocked.Increment(ref _spikesRejected);
            MaybeLogPtpDelta(delta, rejected: true);
            TryReseed(offset, candidateClockId);
            return;
        }

        lock (_lockGate)
        {
            _pendingReseedSamples.Clear();
        }

        var smoothed = ema + (long)(EmaAlpha * (offset - ema));
        Interlocked.Exchange(ref _offsetNs, smoothed);
        Interlocked.Increment(ref _samples);
        MaybeLogPtpDelta(delta, rejected: false);
    }

    /// <summary>
    /// Locks only once <see cref="LockSampleCount"/> samples from the same peer
    /// mutually agree within <see cref="AgreementToleranceNanoseconds"/>, so a single
    /// stray packet from another device cannot seed a bad lock.
    /// </summary>
    private void TryLock(long offset, IPAddress? sourceAddress, ulong candidateClockId)
    {
        var key = sourceAddress ?? IPAddress.Any;
        var locked = false;
        long medianOffset = 0;

        lock (_lockGate)
        {
            if (!_pendingLockSamples.TryGetValue(key, out var samples))
            {
                samples = new List<long>();
                _pendingLockSamples[key] = samples;
            }

            samples.Add(offset);
            if (samples.Count > LockSampleCount)
            {
                samples.RemoveAt(0);
            }

            if (_pendingLockSamples.Count > MaxTrackedPreLockPeers)
            {
                // Bound memory if many stray sources are heard before we lock;
                // keep only the candidate we just heard from.
                _pendingLockSamples.Clear();
                _pendingLockSamples[key] = samples;
            }

            if (samples.Count == LockSampleCount &&
                SamplesAgree(samples, AgreementToleranceNanoseconds, out medianOffset))
            {
                locked = true;
                Volatile.Write(ref _lockedPeerAddress, sourceAddress);
                _pendingLockSamples.Clear();
            }
        }

        if (!locked)
        {
            return;
        }

        Volatile.Write(ref _masterClockId, candidateClockId);
        Interlocked.Exchange(ref _offsetNs, medianOffset);
        Interlocked.Exchange(ref _samples, 1);
        AppLog.Info(
            "ptp",
            $"Locked to grandmaster 0x{MasterClockId:X16} peer={sourceAddress?.ToString() ?? "unknown"} " +
            $"offset={medianOffset / 1_000_000.0:F3} ms");
        _lock?.TrySetResult();
    }

    /// <summary>
    /// Re-seeds only after <see cref="ReseedSampleCount"/> consecutive rejected samples
    /// from the locked peer that themselves mutually agree — i.e. the grandmaster's own
    /// timeline moved, not a transient glitch. Every re-seed is raised as
    /// <see cref="Reseeded"/> so it is observable, never applied silently.
    /// </summary>
    private void TryReseed(long rejectedOffset, ulong candidateClockId)
    {
        var reseeded = false;
        long previousOffset = 0;
        long newOffset = 0;

        lock (_lockGate)
        {
            _pendingReseedSamples.Add((rejectedOffset, candidateClockId));
            if (_pendingReseedSamples.Count > ReseedSampleCount)
            {
                _pendingReseedSamples.RemoveAt(0);
            }

            if (_pendingReseedSamples.Count == ReseedSampleCount)
            {
                var offsets = _pendingReseedSamples.Select(s => s.Offset).ToList();
                if (SamplesAgree(offsets, AgreementToleranceNanoseconds, out newOffset))
                {
                    previousOffset = Interlocked.Read(ref _offsetNs);
                    Interlocked.Exchange(ref _offsetNs, newOffset);
                    Volatile.Write(ref _masterClockId, candidateClockId);
                    _pendingReseedSamples.Clear();
                    reseeded = true;
                }
            }
        }

        if (!reseeded)
        {
            return;
        }

        AppLog.Warn(
            "ptp",
            $"Re-seeded PTP offset after {ReseedSampleCount} consistent rejections " +
            $"old={previousOffset / 1_000_000.0:F3} ms new={newOffset / 1_000_000.0:F3} ms");
        Reseeded?.Invoke(
            this,
            new PtpReseedEventArgs(previousOffset, newOffset, candidateClockId));
    }

    private static bool SamplesAgree(IReadOnlyList<long> samples, long toleranceNanoseconds, out long median)
    {
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        median = sorted[sorted.Length / 2];
        var spread = sorted[^1] - sorted[0];
        return spread <= toleranceNanoseconds;
    }

    private void MaybeLogPtpDelta(long deltaNs, bool rejected)
    {
        // Rate-limit both accepted and rejected samples. Spike rejects used to log
        // every Follow_Up under a noisy link and drowned the Extreme encode path.
        if (!_deltaLog.ShouldLog(out _))
        {
            return;
        }

        if (rejected || deltaNs > 1_000_000)
        {
            AppLog.Info(
                "ptp",
                rejected
                    ? $"Offset spike rejected delta={deltaNs / 1_000_000.0:F3} ms"
                    : $"Offset delta={deltaNs / 1_000_000.0:F3} ms");
        }
    }

    internal static long ReadTimestamp(ReadOnlySpan<byte> source)
    {
        var seconds = ((long)BinaryPrimitives.ReadUInt16BigEndian(source) << 32) |
            BinaryPrimitives.ReadUInt32BigEndian(source[2..]);
        var nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(source[6..]);
        return (seconds * 1_000_000_000L) + nanoseconds;
    }

    /// <summary>Builds a two-step Sync header used by unit tests.</summary>
    internal static byte[] BuildTwoStepSyncForTests(ulong masterClockId, ushort sequence)
    {
        var packet = new byte[44];
        packet[0] = MessageSync | TransportSpecific;
        packet[1] = PtpVersion;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 44);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), FlagTwoStep | (1 << 10));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), masterClockId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28), PortNumber);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), sequence);
        return packet;
    }

    /// <summary>Builds a Follow_Up carrying a precise origin timestamp for unit tests.</summary>
    internal static byte[] BuildFollowUpForTests(
        ulong masterClockId,
        ushort sequence,
        long timestampNanoseconds)
    {
        var packet = new byte[44];
        packet[0] = MessageFollowUp | TransportSpecific;
        packet[1] = PtpVersion;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 44);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 1 << 10);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), masterClockId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28), PortNumber);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), sequence);
        WriteTimestamp(packet.AsSpan(HeaderLength), timestampNanoseconds);
        return packet;
    }

    private static void WriteTimestamp(Span<byte> destination, long nanoseconds)
    {
        var seconds = nanoseconds / 1_000_000_000L;
        var remainder = (uint)(nanoseconds % 1_000_000_000L);
        destination[0] = (byte)(seconds >> 40);
        destination[1] = (byte)(seconds >> 32);
        BinaryPrimitives.WriteUInt32BigEndian(destination[2..], (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], remainder);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown.
            }
        }

        _lock?.TrySetCanceled();
        _event?.Dispose();
        _general?.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>Raised when <see cref="PtpClock"/> re-seeds its offset after a sustained,
/// mutually-consistent disagreement from the locked peer. Represents a clock jump
/// (re-anchor), not a smooth correction.</summary>
public sealed class PtpReseedEventArgs(long previousOffsetNanoseconds, long newOffsetNanoseconds, ulong masterClockId)
    : EventArgs
{
    public long PreviousOffsetNanoseconds { get; } = previousOffsetNanoseconds;

    public long NewOffsetNanoseconds { get; } = newOffsetNanoseconds;

    public ulong MasterClockId { get; } = masterClockId;
}
