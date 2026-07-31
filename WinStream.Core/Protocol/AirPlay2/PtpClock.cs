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

    private static readonly TimeSpan ReceiveFaultBackoff = TimeSpan.FromMilliseconds(50);

    private readonly ulong _clockId;
    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly LogRateLimiter _deltaLog = new(TimeSpan.FromSeconds(1));
    private UdpClient? _event;
    private UdpClient? _general;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private IPEndPoint? _peerEvent;
    private TaskCompletionSource? _lock;
    private long _syncArrivalNs;
    private ushort _syncSequence;
    private bool _syncPending;
    private long _offsetNs;
    private ulong _masterClockId;
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

    public bool IsLocked => Volatile.Read(ref _samples) > 0;

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
    internal void HandleIncomingForTests(byte[] packet, long arrivalNs) =>
        HandleIncoming(packet, arrivalNs);

    /// <summary>Test seam: set a synthetic offset without UDP.</summary>
    internal void SetOffsetForTests(long offsetNs, ulong masterClockId)
    {
        Interlocked.Exchange(ref _offsetNs, offsetNs);
        Volatile.Write(ref _masterClockId, masterClockId);
        if (Interlocked.CompareExchange(ref _samples, 1, 0) == 0)
        {
            _lock?.TrySetResult();
        }
    }

    /// <summary>Test seam: apply a master/local pair through the production smoother.</summary>
    internal void ApplyOffsetForTests(long masterNs, long localNs) =>
        ApplyOffset(masterNs, localNs);

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
                HandleIncoming(datagram.Buffer, (long)_mono.Elapsed.TotalNanoseconds);
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

    private void HandleIncoming(byte[] packet, long arrivalNs)
    {
        if (packet.Length < HeaderLength)
        {
            return;
        }

        var messageType = (byte)(packet[0] & 0x0f);
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(30));
        switch (messageType)
        {
            case MessageAnnounce:
            case MessageSync when packet.Length >= HeaderLength + 10:
                Volatile.Write(
                    ref _masterClockId,
                    BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(20)));
                break;
        }

        switch (messageType)
        {
            case MessageSync:
                HandleSync(packet, sequence, arrivalNs);
                break;
            case MessageFollowUp when packet.Length >= HeaderLength + 10:
                HandleFollowUp(packet, sequence);
                break;
        }
    }

    private void HandleSync(byte[] packet, ushort sequence, long arrivalNs)
    {
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6));
        if ((flags & FlagTwoStep) != 0)
        {
            lock (_gate)
            {
                _syncArrivalNs = arrivalNs;
                _syncSequence = sequence;
                _syncPending = true;
            }

            return;
        }

        if (packet.Length >= HeaderLength + 10)
        {
            ApplyOffset(ReadTimestamp(packet.AsSpan(HeaderLength, 10)), arrivalNs);
        }
    }

    private void HandleFollowUp(byte[] packet, ushort sequence)
    {
        long arrival;
        lock (_gate)
        {
            if (!_syncPending || _syncSequence != sequence)
            {
                return;
            }

            arrival = _syncArrivalNs;
            _syncPending = false;
        }

        ApplyOffset(ReadTimestamp(packet.AsSpan(HeaderLength, 10)), arrival);
    }

    private void ApplyOffset(long masterNs, long localNs)
    {
        var offset = masterNs - localNs;
        var samples = Volatile.Read(ref _samples);
        if (samples == 0)
        {
            Interlocked.Exchange(ref _offsetNs, offset);
            Interlocked.Exchange(ref _samples, 1);
            AppLog.Info(
                "ptp",
                $"Locked to grandmaster 0x{MasterClockId:X16} offset={offset / 1_000_000.0:F3} ms");
            _lock?.TrySetResult();
            return;
        }

        var ema = Interlocked.Read(ref _offsetNs);
        var delta = Math.Abs(offset - ema);
        if (delta > SpikeRejectNanoseconds)
        {
            Interlocked.Increment(ref _spikesRejected);
            MaybeLogPtpDelta(delta, rejected: true);
            return;
        }

        var smoothed = ema + (long)(EmaAlpha * (offset - ema));
        Interlocked.Exchange(ref _offsetNs, smoothed);
        Interlocked.Increment(ref _samples);
        MaybeLogPtpDelta(delta, rejected: false);
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
