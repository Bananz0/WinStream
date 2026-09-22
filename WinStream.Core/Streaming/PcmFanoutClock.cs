using System.Security.Cryptography;

namespace WinStream.Core.Streaming;

/// <summary>
/// Shared media clock so every fan-out consumer sees the same stamp for a PCM tick.
/// </summary>
public sealed class PcmFanoutClock
{
    private readonly object _gate = new();
    private uint _timestamp;

    /// <summary>
    /// Starts at a random RTP timestamp in the middle of the uint range so
    /// receivers that subtract a few seconds of latency never wrap under zero.
    /// Passing 0 still means "pick a safe random base", not literal zero.
    /// </summary>
    public PcmFanoutClock(uint initialTimestamp = 0)
    {
        _timestamp = initialTimestamp == 0
            ? (uint)RandomNumberGenerator.GetInt32(1 << 20, int.MaxValue)
            : initialTimestamp;
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

    public uint Advance(uint frames)
    {
        lock (_gate)
        {
            var stamp = _timestamp;
            _timestamp += frames;
            return stamp;
        }
    }

    public void Reset(uint timestamp = 0)
    {
        lock (_gate)
        {
            _timestamp = timestamp == 0
                ? (uint)RandomNumberGenerator.GetInt32(1 << 20, int.MaxValue)
                : timestamp;
        }
    }
}
