namespace WinStream.Core.Audio;

/// <summary>
/// Moves PCM frames off the capture thread: producers only enqueue; a worker invokes send.
/// </summary>
public sealed class AudioFrameSendPump : IAsyncDisposable
{
    private readonly BoundedAudioFrameQueue _queue;
    private readonly Action<AudioFrame> _send;
    private readonly ManualResetEventSlim _workerGate = new(true);
    private ManualResetEventSlim? _workerEnteredGate;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private long _sendCount;
    private bool _disposed;

    public AudioFrameSendPump(int capacity, Action<AudioFrame> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _queue = new BoundedAudioFrameQueue(capacity);
        _send = send;
    }

    public long QueueDropCount => _queue.DropCount;

    public int QueueDepth => _queue.Count;

    public long SendCount => Interlocked.Read(ref _sendCount);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cts is not null)
        {
            return;
        }

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
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var frame))
            {
                try
                {
                    Task.Delay(1, cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
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

            var started = Environment.TickCount64;
            try
            {
                _send(frame);
                Interlocked.Increment(ref _sendCount);
            }
            catch (Exception ex)
            {
                Logging.AppLog.Error(
                    "stream",
                    $"Encode/send failed; continuing pump: {ex.GetType().Name}: {ex.Message}");
            }

            var elapsed = Environment.TickCount64 - started;
            if (elapsed >= 8)
            {
                Logging.AppLog.Info("stream", $"Encode+send took {elapsed} ms");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workerGate.Set();
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
    }
}
