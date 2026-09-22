using System.Collections.Generic;

namespace WinStream.Core.Audio;

/// <summary>
/// Bounded PCM frame queue with drop-oldest overflow. Capture enqueues; a worker dequeues.
/// </summary>
public sealed class BoundedAudioFrameQueue
{
    private readonly Queue<AudioFrame> _queue;
    private readonly int _capacity;
    private readonly object _gate = new();
    private long _dropCount;

    public BoundedAudioFrameQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _queue = new Queue<AudioFrame>(capacity);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public long DropCount => Interlocked.Read(ref _dropCount);

    /// <summary>
    /// Enqueues <paramref name="frame"/>. If full, drops the oldest frame first.
    /// Never blocks.
    /// </summary>
    public void Enqueue(AudioFrame frame)
    {
        lock (_gate)
        {
            while (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _dropCount);
            }

            _queue.Enqueue(frame);
        }
    }

    public bool TryDequeue(out AudioFrame frame)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _queue.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
        }
    }
}
