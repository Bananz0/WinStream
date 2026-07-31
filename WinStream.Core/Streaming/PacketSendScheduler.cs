using WinStream.Core.Protocol.Raop;

namespace WinStream.Core.Streaming;

/// <summary>
/// Absolute send timeline for RTP audio. Deadlines derive from the cumulative
/// output frame count rather than elapsed wall time, so scheduling error cannot
/// accumulate the way a chain of relative sleeps does.
/// </summary>
/// <remarks>
/// Callers supply <c>nowTicks</c> in <see cref="TimeSpan"/> ticks (100 ns), which
/// keeps the type free of any clock dependency and trivial to drive from a test.
/// </remarks>
public sealed class PacketSendScheduler
{
    /// <summary>
    /// How far behind the timeline the sender may fall before it re-anchors. Past
    /// this, the backlog is stale: releasing it back-to-back would burst the
    /// network instead of catching up.
    /// </summary>
    public const int MaxCatchUpPackets = 3;

    private const int TargetSampleRate = 44100;

    private static readonly long MaxLatenessTicks =
        MaxCatchUpPackets * TimeSpan.TicksPerSecond * AlacEncoder.FramesPerPacket /
        TargetSampleRate;

    private long _anchorTicks;
    private long _framesSinceAnchor;
    private bool _anchored;

    /// <summary>Ticks of audio one packet represents; the pacing quantum.</summary>
    public static long PacketPeriodTicks { get; } =
        TimeSpan.TicksPerSecond * AlacEncoder.FramesPerPacket / TargetSampleRate;

    /// <summary>How many times the timeline has been re-anchored after falling behind.</summary>
    public long CatchUpClampCount { get; private set; }

    public void Reset(long nowTicks)
    {
        _anchorTicks = nowTicks;
        _framesSinceAnchor = 0;
        _anchored = true;
    }

    /// <summary>
    /// Ticks to wait before releasing <paramref name="outputFrames"/> of 44.1 kHz
    /// audio, then advances the timeline by that much. Returns zero when already
    /// due. Accounting in frames rather than whole packets keeps a resampled
    /// source (48 kHz, say) from drifting against real time.
    /// </summary>
    public long TakeWaitTicks(long nowTicks, int outputFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputFrames);

        if (!_anchored)
        {
            Reset(nowTicks);
        }

        var dueTicks = _anchorTicks +
            (_framesSinceAnchor * TimeSpan.TicksPerSecond / TargetSampleRate);
        var waitTicks = dueTicks - nowTicks;

        if (waitTicks < -MaxLatenessTicks)
        {
            Reset(nowTicks);
            CatchUpClampCount++;
            waitTicks = 0;
        }

        _framesSinceAnchor += outputFrames;
        return waitTicks > 0 ? waitTicks : 0;
    }
}
