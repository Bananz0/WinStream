using WinStream.Core.Audio;

namespace WinStream.Core.Audio;

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
    /// Default lateness budget (in packets) used until <see cref="SetLatenessBudget"/>
    /// is called. Kept only as the pre-Auto-latency fallback; real callers derive the
    /// budget from the current effective latency instead of this fixed constant.
    /// </summary>
    public const int MaxCatchUpPackets = 3;

    private const int TargetSampleRate = 44100;

    private static readonly long DefaultMaxLatenessTicks =
        MaxCatchUpPackets * TimeSpan.TicksPerSecond * AudioPacingConstants.PacketFrames /
        TargetSampleRate;

    private long _anchorTicks;
    private long _framesSinceAnchor;
    private bool _anchored;
    private long _stallCount;
    private long _maxLatenessTicks = DefaultMaxLatenessTicks;

    /// <summary>Ticks of audio one packet represents; the pacing quantum.</summary>
    public static long PacketPeriodTicks { get; } =
        TimeSpan.TicksPerSecond * AudioPacingConstants.PacketFrames / TargetSampleRate;

    /// <summary>
    /// How many times the timeline has fallen behind by more than the lateness budget
    /// and been re-anchored, discarding the stale backlog. A caller (e.g. the Auto
    /// latency controller) should treat each increment as a reason to raise one rung
    /// "inside the hole" rather than waiting for a slower, drift-based signal.
    /// </summary>
    public long StallCount => Interlocked.Read(ref _stallCount);

    /// <summary>
    /// Legacy alias for <see cref="StallCount"/>, kept so existing callers (live-quality
    /// reporting) keep working unchanged while newer code migrates to the new name.
    /// </summary>
    public long CatchUpClampCount => StallCount;

    /// <summary>
    /// Sets how far behind the timeline the sender may fall before it re-anchors,
    /// expressed in output frames rather than a fixed packet count so it can track the
    /// current effective latency (e.g. <c>effectiveLatencyFrames - captureP95Frames -
    /// 16msFrames</c>) instead of the old constant 3-packet allowance. A stall within
    /// this budget is caught up by bursting the backlog (no artificial wait, since
    /// <see cref="TakeWaitTicks"/> already returns zero while behind but inside budget);
    /// a stall beyond it re-anchors and discards the stale backlog via <see cref="Reset"/>.
    /// </summary>
    public void SetLatenessBudget(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        Interlocked.Exchange(
            ref _maxLatenessTicks,
            frames * TimeSpan.TicksPerSecond / TargetSampleRate);
    }

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
        var maxLatenessTicks = Interlocked.Read(ref _maxLatenessTicks);

        if (waitTicks < -maxLatenessTicks)
        {
            Reset(nowTicks);
            Interlocked.Increment(ref _stallCount);
            waitTicks = 0;
        }

        _framesSinceAnchor += outputFrames;
        return waitTicks > 0 ? waitTicks : 0;
    }

    /// <summary>
    /// Advances the timeline by <paramref name="outputFrames"/> as if they had gone out
    /// exactly on schedule, without computing a wait or evaluating the lateness budget.
    /// Used for content that is deliberately emitted as a single burst rather than paced
    /// — e.g. gap-fill silence bridging a capture gap — so the fill itself never counts
    /// as backlog and never trips <see cref="StallCount"/>. The anchor line stays intact:
    /// once the burst ends, the next real frame's due time reflects true elapsed time, and
    /// ordinary catch-up/stall handling in <see cref="TakeWaitTicks"/> resumes from there.
    /// </summary>
    public void AdvanceForBurst(int outputFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputFrames);
        if (!_anchored)
        {
            Reset(0);
        }

        _framesSinceAnchor += outputFrames;
    }
}
