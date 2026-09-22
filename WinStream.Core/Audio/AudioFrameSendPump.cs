using System.Diagnostics;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Core.Audio;

/// <summary>
/// Moves PCM frames off the capture thread and releases them on an absolute
/// timeline: producers only enqueue; a worker slices each frame into
/// packet-sized chunks and invokes send as each chunk comes due.
/// </summary>
/// <remarks>
/// Pacing lives here rather than inside each session because the worker fans one
/// frame out to every receiver in turn. Waiting per session would make the second
/// receiver queue behind the first receiver's waits.
/// </remarks>
public sealed class AudioFrameSendPump : IAsyncDisposable
{
    private readonly BoundedAudioFrameQueue _queue;
    private readonly Action<AudioFrame> _send;
    private readonly Func<IDisposable?>? _elevateCurrentThread;
    private readonly Func<long, CancellationToken, bool>? _waitUntilDue;
    private readonly PacketSendScheduler _scheduler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _workerGate = new(true);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Logging.LogRateLimiter _slowSendLog =
        new(TimeSpan.FromMilliseconds(SlowSendLogIntervalMilliseconds));

    private ManualResetEventSlim? _workerEnteredGate;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private long _sendCount;
    private long _slowSendCount;
    private bool _disposed;

    /// <summary>
    /// Encode+send longer than this counts as pressure. Must use <see cref="Stopwatch"/>,
    /// never <see cref="Environment.TickCount64"/> — tick count often advances in ~15.6 ms
    /// steps, which falsely reports every borderline send as a 15 ms stall under Extreme.
    /// </summary>
    public const double SlowSendMilliseconds = 8;

    private const int SlowSendLogIntervalMilliseconds = 5_000;

    /// <summary>
    /// A blocking wait rounds up to the OS timer quantum (~15.6 ms), longer than a
    /// whole packet. The last millisecond is closed with a yielding spin instead.
    /// </summary>
    private const long SpinFloorTicks = TimeSpan.TicksPerMillisecond;

    /// <param name="elevateCurrentThread">
    /// Invoked once on the worker thread before the first send, so the app layer can
    /// register it with MMCSS. Core stays free of Win32 interop. Anything returned is
    /// disposed on that same thread when the worker stops.
    /// </param>
    /// <param name="waitUntilDue">
    /// Overrides how the worker waits for a packet's deadline. Returns false when
    /// cancelled. The app layer supplies a high-resolution timer here; the
    /// built-in hybrid wait is the fallback when none is given.
    /// </param>
    public AudioFrameSendPump(
        int capacity,
        Action<AudioFrame> send,
        Func<IDisposable?>? elevateCurrentThread = null,
        Func<long, CancellationToken, bool>? waitUntilDue = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        _queue = new BoundedAudioFrameQueue(capacity);
        _send = send;
        _elevateCurrentThread = elevateCurrentThread;
        _waitUntilDue = waitUntilDue;
    }

    public long QueueDropCount => _queue.DropCount;

    public int QueueDepth => _queue.Count;

    public long SendCount => Interlocked.Read(ref _sendCount);

    /// <summary>How many encode+send operations took ≥ 8 ms (Auto latency pressure signal).</summary>
    public long SlowSendCount => Interlocked.Read(ref _slowSendCount);

    /// <summary>How often the send timeline re-anchored after falling too far behind.</summary>
    public long CatchUpClampCount => _scheduler.CatchUpClampCount;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cts is not null)
        {
            return;
        }

        _scheduler.Reset(_clock.Elapsed.Ticks);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _worker = Task.Factory.StartNew(
            () => RunWorker(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Capture-thread entry: never calls <see cref="_send"/>.</summary>
    public void Enqueue(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queue.Enqueue(frame);
        _ready.Set();
    }

    /// <summary>Test seam: block the worker before/while sending.</summary>
    internal void BlockWorkerForTests() => _workerGate.Reset();

    /// <summary>Test seam: allow the worker to proceed.</summary>
    internal void UnblockWorkerForTests() => _workerGate.Set();

    /// <summary>Test seam: signaled once the worker is about to wait on the gate.</summary>
    internal ManualResetEventSlim ArmWorkerEnteredSignalForTests()
    {
        _workerEnteredGate = new ManualResetEventSlim(false);
        return _workerEnteredGate;
    }

    private void RunWorker(CancellationToken cancellationToken)
    {
        // LongRunning gives this a dedicated thread, so a priority registration made
        // here stays bound to the thread that actually sends, and is released on it.
        using var elevation = TryElevateThread();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var frame))
            {
                // Event wake instead of Task.Delay(1): a 1 ms sleep adds timer jitter
                // and can burn a meaningful fraction of an Extreme (~8 ms) packet period.
                _ready.Reset();
                if (_queue.TryDequeue(out frame))
                {
                    // Item arrived between the empty check and Reset.
                }
                else
                {
                    try
                    {
                        _ready.Wait(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }
            }

            try
            {
                _workerEnteredGate?.Set();
                _workerGate.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!SendFramePaced(frame, cancellationToken))
            {
                return;
            }
        }
    }

    private IDisposable? TryElevateThread()
    {
        if (_elevateCurrentThread is null)
        {
            return null;
        }

        try
        {
            return _elevateCurrentThread();
        }
        catch (Exception ex)
        {
            Logging.AppLog.Warn(
                "stream",
                $"Send thread priority not raised; continuing: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Releases one capture frame as packet-sized chunks on the timeline.</summary>
    private bool SendFramePaced(AudioFrame frame, CancellationToken cancellationToken)
    {
        var format = frame.Format;
        var bytesPerSourceFrame = format.Channels * (format.BitsPerSample / 8);
        if (bytesPerSourceFrame <= 0 || frame.Pcm.Length <= 0)
        {
            SendChunk(frame);
            return true;
        }

        // One chunk yields one 352-frame packet after resample. Exactness is not
        // required: PcmPacketBuffer carries the remainder, and the scheduler paces
        // on frames actually handed over, so a non-44.1 source cannot drift.
        var chunkSourceFrames = Math.Max(
            1,
            AudioPacingConstants.PacketFrames * format.SampleRate / 44100);
        var chunkBytes = chunkSourceFrames * bytesPerSourceFrame;

        var offset = 0;
        while (offset < frame.Pcm.Length)
        {
            var length = Math.Min(chunkBytes, frame.Pcm.Length - offset);
            var outputFrames = (int)PcmPacketBuffer.EstimateOutputFrames(length, format);
            var waitTicks = _scheduler.TakeWaitTicks(_clock.Elapsed.Ticks, outputFrames);
            if (!WaitUntilDue(waitTicks, cancellationToken))
            {
                return false;
            }

            SendChunk(new AudioFrame(
                frame.Pcm.Slice(offset, length),
                format,
                frame.TimestampTicks));
            offset += length;
        }

        return true;
    }

    private void SendChunk(AudioFrame chunk)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            _send(chunk);
            Interlocked.Increment(ref _sendCount);
        }
        catch (Exception ex)
        {
            Logging.AppLog.Error(
                "stream",
                $"Encode/send failed; continuing pump: {ex.GetType().Name}: {ex.Message}");
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= SlowSendMilliseconds)
        {
            Interlocked.Increment(ref _slowSendCount);
            MaybeLogSlowSend(elapsedMs);
        }
    }

    private bool WaitUntilDue(long waitTicks, CancellationToken cancellationToken)
    {
        if (_waitUntilDue is not null)
        {
            return _waitUntilDue(waitTicks, cancellationToken);
        }

        if (waitTicks <= 0)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        var dueTicks = _clock.Elapsed.Ticks + waitTicks;
        var spinner = new SpinWait();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var remainingTicks = dueTicks - _clock.Elapsed.Ticks;
            if (remainingTicks <= 0)
            {
                return true;
            }

            if (remainingTicks > SpinFloorTicks)
            {
                var blockMs = (int)((remainingTicks - SpinFloorTicks) / TimeSpan.TicksPerMillisecond);
                if (blockMs > 0 && cancellationToken.WaitHandle.WaitOne(blockMs))
                {
                    return false;
                }
            }
            else
            {
                // sleep1Threshold -1 keeps SpinOnce off Thread.Sleep(1), which would
                // overshoot the deadline by a whole timer quantum.
                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    private void MaybeLogSlowSend(double elapsedMs)
    {
        if (!_slowSendLog.ShouldLog(out var suppressed))
        {
            return;
        }

        Logging.AppLog.Info(
            "stream",
            suppressed > 0
                ? $"Encode+send took {elapsedMs:F1} ms (suppressed {suppressed} similar)"
                : $"Encode+send took {elapsedMs:F1} ms");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workerGate.Set();
        _ready.Set();
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _cts?.Dispose();
        _cts = null;
        _worker = null;
        _queue.Clear();
        _workerGate.Dispose();
        _ready.Dispose();
    }
}
