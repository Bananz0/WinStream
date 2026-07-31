using WinStream.Core.Audio;

namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Receiver-side playout gate shared by every Link RX so Windows and Pi cannot drift
/// apart: prime to the jitter target, release while healthy, and re-prime when the
/// controller grows the target after late packets or sink starvation.
/// </summary>
public sealed class LinkPlayoutBuffer
{
    private readonly Queue<byte[]> _queue = new();
    private readonly LinkJitterController _jitter;
    private readonly AudioFormat _format;
    private readonly int _capacityBytes;
    private ushort? _lastSequence;
    private int _queuedBytes;

    public LinkPlayoutBuffer(
        AudioFormat format,
        bool pathIsEthernet,
        DateTimeOffset startedUtc,
        int capacityMilliseconds = DefaultCapacityMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityMilliseconds, LinkJitterController.MaxMs);
        _format = format;
        _capacityBytes = BytesForMilliseconds(format, capacityMilliseconds);
        _jitter = new LinkJitterController(pathIsEthernet);
        _jitter.MarkStarted(startedUtc);
    }

    public const int DefaultCapacityMilliseconds = 200;

    public int TargetMilliseconds => _jitter.TargetMilliseconds;

    public int TargetBytes => BytesForMilliseconds(_format, _jitter.TargetMilliseconds);

    /// <summary>False while priming; the sink must stay silent until this flips.</summary>
    public bool IsPlaying { get; private set; }

    public int QueuedBytes => _queuedBytes;

    public long PacketsAccepted { get; private set; }

    /// <summary>Sequence gaps: reordered, lost, or late packets.</summary>
    public long LateOrLostPackets { get; private set; }

    public long Underruns { get; private set; }

    /// <summary>Bytes dropped because the queue hit its ceiling.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Times the controller grew the target and forced a re-prime.</summary>
    public long Repriming { get; private set; }

    /// <param name="sinkStarved">
    /// True when the sink ran dry since the last packet. Receivers that cannot observe
    /// their sink leave this false and rely on sequence gaps alone.
    /// </param>
    public LinkPlayoutPush Push(
        ushort sequence,
        ReadOnlySpan<byte> payload,
        DateTimeOffset utcNow,
        bool sinkStarved = false)
    {
        PacketsAccepted++;

        var late = _lastSequence is not null && sequence != (ushort)(_lastSequence.Value + 1);
        if (late)
        {
            LateOrLostPackets++;
        }

        _lastSequence = sequence;

        var starved = sinkStarved && IsPlaying;
        if (starved)
        {
            Underruns++;
        }

        var previousTarget = _jitter.TargetMilliseconds;
        _jitter.TryUpdate(late || starved, utcNow);
        var grew = _jitter.TargetMilliseconds > previousTarget;
        var paused = false;
        if (grew && IsPlaying)
        {
            IsPlaying = false;
            Repriming++;
            paused = true;
        }

        Enqueue(payload);

        var started = false;
        if (!IsPlaying && _queuedBytes >= TargetBytes)
        {
            IsPlaying = true;
            started = true;
        }

        return new LinkPlayoutPush(late, starved, paused, started);
    }

    /// <summary>Pops the next chunk to hand the sink, or nothing while priming.</summary>
    public bool TryDrain(out byte[] chunk)
    {
        if (!IsPlaying || _queue.Count == 0)
        {
            chunk = Array.Empty<byte>();
            return false;
        }

        chunk = _queue.Dequeue();
        _queuedBytes -= chunk.Length;
        return true;
    }

    private void Enqueue(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        // Dropping the oldest audio keeps latency bounded; keeping it would trade the
        // whole point of this path for continuity.
        while (_queue.Count > 0 && _queuedBytes + payload.Length > _capacityBytes)
        {
            var evicted = _queue.Dequeue();
            _queuedBytes -= evicted.Length;
            DroppedBytes += evicted.Length;
        }

        _queue.Enqueue(payload.ToArray());
        _queuedBytes += payload.Length;
    }

    private static int BytesForMilliseconds(AudioFormat format, int milliseconds) =>
        format.AverageBytesPerSecond * milliseconds / 1000;
}

/// <summary>What a single <see cref="LinkPlayoutBuffer.Push"/> observed.</summary>
public readonly record struct LinkPlayoutPush(
    bool WasLate,
    bool WasStarved,
    bool PausedForReprime,
    bool StartedPlayout);
