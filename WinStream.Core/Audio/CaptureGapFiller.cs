namespace WinStream.Core.Audio;

/// <summary>
/// Builds PCM16 silence to bridge WASAPI loopback gaps so RTP stays continuous.
/// </summary>
public static class CaptureGapFiller
{
    /// <summary>
    /// Legacy fixed threshold, kept as the fallback for callers that do not track jitter.
    /// Tuned for the old 50ms-interval polling capture; Auto mode's 10ms event-driven
    /// capture should use <see cref="JitterTracker"/> instead, since a fixed 120ms
    /// threshold leaves any 48-120ms hiccup as an unfilled underrun in that mode.
    /// </summary>
    public const double ThresholdMilliseconds = 120;

    /// <summary>
    /// Floor for the jitter-derived threshold. Must stay above the tightest real
    /// event-driven callback spacing so ordinary scheduling noise is never flagged.
    /// </summary>
    public const double MinAdaptiveThresholdMilliseconds = 40;

    /// <summary>
    /// Ceiling for the jitter-derived threshold, matching the old fixed constant so a
    /// very jittery (e.g. polling) source never waits longer than before to be filled.
    /// </summary>
    public const double MaxAdaptiveThresholdMilliseconds = 120;

    /// <summary>
    /// The adaptive threshold is this many multiples of the largest recently observed
    /// healthy inter-callback interval, so normal jitter is never itself flagged as a gap.
    /// </summary>
    public const double ThresholdJitterMultiplier = 2.0;

    /// <summary>
    /// Number of recent healthy inter-callback intervals kept to derive the threshold.
    /// </summary>
    public const int JitterWindowSize = 32;

    public const double MaxFillMilliseconds = 2000;
    public const double ChunkMilliseconds = 10;

    public static bool IsGap(long deltaTicks, long frequencyHz) =>
        GapMilliseconds(deltaTicks, frequencyHz) > ThresholdMilliseconds;

    public static bool IsGap(long deltaTicks, long frequencyHz, double thresholdMilliseconds) =>
        GapMilliseconds(deltaTicks, frequencyHz) > thresholdMilliseconds;

    public static double GapMilliseconds(long deltaTicks, long frequencyHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequencyHz, 0);
        if (deltaTicks <= 0)
        {
            return 0;
        }

        return deltaTicks * 1000.0 / frequencyHz;
    }

    /// <summary>
    /// Marks the start of a gap once. Returns true when this call opens a new gap.
    /// </summary>
    public static bool TryBeginGap(ref int inGapFlag, ref long gapCount)
    {
        if (Interlocked.CompareExchange(ref inGapFlag, 1, 0) != 0)
        {
            return false;
        }

        Interlocked.Increment(ref gapCount);
        return true;
    }

    public static void EndGap(ref int inGapFlag) =>
        Interlocked.Exchange(ref inGapFlag, 0);

    /// <summary>
    /// Zeroed PCM16 covering <paramref name="gapMilliseconds"/> (capped).
    /// </summary>
    public static byte[] CreateSilence(AudioFormat format, double gapMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(format);
        var ms = Math.Clamp(gapMilliseconds, 0, MaxFillMilliseconds);
        var frames = (int)Math.Round(format.SampleRate * ms / 1000.0);
        if (frames <= 0)
        {
            return Array.Empty<byte>();
        }

        return new byte[frames * format.BlockAlign];
    }

    /// <summary>
    /// Splits a (capped) gap into <see cref="ChunkMilliseconds"/>-sized silence frames,
    /// emitted as a single burst instead of one frame per real-time timer tick. Pacing the
    /// fill to wall-clock time lets a long gap keep the send queue artificially shallow
    /// while it is open and then arrive all at once when the timer catches up; handing the
    /// caller the whole burst up front means the queue depth reflects the gap once, not
    /// once per tick.
    /// </summary>
    public static IReadOnlyList<byte[]> CreateBurstSilence(AudioFormat format, double gapMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(format);
        var ms = Math.Clamp(gapMilliseconds, 0, MaxFillMilliseconds);
        if (ms <= 0)
        {
            return Array.Empty<byte[]>();
        }

        var chunks = new List<byte[]>((int)Math.Ceiling(ms / ChunkMilliseconds) + 1);
        var remaining = ms;
        while (remaining > 0)
        {
            var chunkMs = Math.Min(ChunkMilliseconds, remaining);
            var chunk = CreateSilence(format, chunkMs);
            if (chunk.Length == 0)
            {
                break;
            }

            chunks.Add(chunk);
            remaining -= chunkMs;
        }

        return chunks;
    }

    /// <summary>
    /// Tracks recent healthy inter-callback spacing and derives a gap threshold that
    /// scales with measured capture jitter instead of a fixed constant, so a capture mode
    /// with tight, consistent spacing (Auto's 10ms event-driven callbacks) gets a low
    /// threshold that catches short hiccups, while a mode with naturally looser spacing
    /// (50ms polling) gets a higher threshold so its normal cadence is never flagged.
    /// </summary>
    public sealed class JitterTracker
    {
        private readonly TimeProvider _timeProvider;
        private readonly double[] _samples;
        private readonly double _multiplier;
        private readonly double _minThresholdMilliseconds;
        private readonly double _maxThresholdMilliseconds;
        private int _count;
        private int _next;
        private DateTimeOffset? _lastCallbackUtc;

        public JitterTracker(
            TimeProvider? timeProvider = null,
            int windowSize = JitterWindowSize,
            double multiplier = ThresholdJitterMultiplier,
            double minThresholdMilliseconds = MinAdaptiveThresholdMilliseconds,
            double maxThresholdMilliseconds = MaxAdaptiveThresholdMilliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(multiplier, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                minThresholdMilliseconds, maxThresholdMilliseconds);

            _timeProvider = timeProvider ?? TimeProvider.System;
            _samples = new double[windowSize];
            _multiplier = multiplier;
            _minThresholdMilliseconds = minThresholdMilliseconds;
            _maxThresholdMilliseconds = maxThresholdMilliseconds;
        }

        /// <summary>
        /// The current adaptive threshold in milliseconds, derived from the largest
        /// recently observed healthy interval, clamped to the configured bounds. Before
        /// any samples are recorded this falls back to <see cref="MaxAdaptiveThresholdMilliseconds"/>
        /// so an unwarmed tracker never fires early on ordinary startup jitter.
        /// </summary>
        public double CurrentThresholdMilliseconds
        {
            get
            {
                if (_count == 0)
                {
                    return _maxThresholdMilliseconds;
                }

                var maxSample = 0.0;
                for (var i = 0; i < _count; i++)
                {
                    maxSample = Math.Max(maxSample, _samples[i]);
                }

                return Math.Clamp(
                    maxSample * _multiplier, _minThresholdMilliseconds, _maxThresholdMilliseconds);
            }
        }

        /// <summary>
        /// Records a healthy (non-gap) callback arriving now, updating the jitter window
        /// from the interval since the previous recorded callback. Call this every time a
        /// real capture callback closes a gap, mirroring <see cref="EndGap"/>.
        /// </summary>
        public void RecordCallback()
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastCallbackUtc is { } last)
            {
                var elapsedMs = (now - last).TotalMilliseconds;
                if (elapsedMs > 0 && elapsedMs <= CurrentThresholdMilliseconds)
                {
                    _samples[_next] = elapsedMs;
                    _next = (_next + 1) % _samples.Length;
                    if (_count < _samples.Length)
                    {
                        _count++;
                    }
                }
            }

            _lastCallbackUtc = now;
        }

        /// <summary>
        /// True when more time has elapsed since the last recorded callback than the
        /// current adaptive threshold allows.
        /// </summary>
        public bool IsGapNow()
        {
            if (_lastCallbackUtc is not { } last)
            {
                return false;
            }

            var elapsedMs = (_timeProvider.GetUtcNow() - last).TotalMilliseconds;
            return elapsedMs > CurrentThresholdMilliseconds;
        }
    }
}
