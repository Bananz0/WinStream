namespace WinStream.Core.Streaming;

public readonly record struct FanoutTick(uint Timestamp, long HostTimestamp);

/// <summary>
/// Shared media clock so every fan-out consumer sees the same stamp for a PCM tick.
/// </summary>
public sealed class PcmFanoutClock
{
    private readonly object _gate = new();
    private uint _timestamp;

    public PcmFanoutClock(uint initialTimestamp = 0)
    {
        _timestamp = initialTimestamp;
    }

    public uint CurrentTimestamp
    {
        get
        {
            lock (_gate)
            {
                return _timestamp;
            }
        }
    }

    public FanoutTick Peek()
    {
        lock (_gate)
        {
            return new FanoutTick(_timestamp, Environment.TickCount64);
        }
    }

    public FanoutTick Advance(uint frames)
    {
        lock (_gate)
        {
            var tick = new FanoutTick(_timestamp, Environment.TickCount64);
            _timestamp += frames;
            return tick;
        }
    }

    public void Reset(uint timestamp = 0)
    {
        lock (_gate)
        {
            _timestamp = timestamp;
        }
    }
}
