using System.Diagnostics;
using System.Linq;
using WinStream.Core.Audio;

namespace WinStream.Tests;

public class CaptureGapFillerTests
{
    [Fact]
    public void IsGap_detects_threshold()
    {
        const long freq = 10_000_000; // 100 ns ticks
        var under = (long)(100.0 / 1000.0 * freq);
        var over = (long)(140.0 / 1000.0 * freq);
        Assert.False(CaptureGapFiller.IsGap(under, freq));
        Assert.True(CaptureGapFiller.IsGap(over, freq));
    }

    [Fact]
    public void IsGap_ignores_normal_loopback_callback_cadence()
    {
        // NAudio polls the loopback client every half buffer and Windows rounds the sleep
        // up to the ~15.6 ms timer tick, so healthy callbacks land tens of ms apart.
        const long freq = 10_000_000;
        foreach (var callbackMs in new[] { 25.0, 31.25, 50.0, 62.5 })
        {
            var delta = (long)(callbackMs / 1000.0 * freq);
            Assert.False(
                CaptureGapFiller.IsGap(delta, freq),
                $"{callbackMs} ms callback must not be treated as a gap.");
        }
    }

    [Fact]
    public void CreateSilence_covers_gap_duration()
    {
        var format = new AudioFormat(44100, 2, 16);
        var pcm = CaptureGapFiller.CreateSilence(format, gapMilliseconds: 100);
        // 44100 * 0.1 * 4 bytes ≈ 17640
        Assert.Equal(44100 * 4 / 10, pcm.Length);
        Assert.All(pcm, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CreateSilence_caps_at_two_seconds()
    {
        var format = new AudioFormat(44100, 2, 16);
        var pcm = CaptureGapFiller.CreateSilence(format, gapMilliseconds: 10_000);
        Assert.Equal(44100 * 2 * 4, pcm.Length);
    }

    [Fact]
    public void TryBeginGap_increments_once_per_gap()
    {
        var inGap = 0;
        long gapCount = 0;
        Assert.True(CaptureGapFiller.TryBeginGap(ref inGap, ref gapCount));
        Assert.False(CaptureGapFiller.TryBeginGap(ref inGap, ref gapCount));
        Assert.Equal(1, gapCount);

        CaptureGapFiller.EndGap(ref inGap);
        Assert.True(CaptureGapFiller.TryBeginGap(ref inGap, ref gapCount));
        Assert.Equal(2, gapCount);
    }

    [Fact]
    public void Synthetic_gap_insert_increments_discontinuity_model()
    {
        // Production seam used by WasapiLoopbackSource gap timer.
        var inGap = 0;
        long gapCount = 0;
        var format = new AudioFormat(44100, 2, 16);
        var frames = new List<AudioFrame>();

        Assert.True(CaptureGapFiller.IsGap(
            deltaTicks: (long)(200.0 / 1000.0 * Stopwatch.Frequency),
            frequencyHz: Stopwatch.Frequency));

        Assert.True(CaptureGapFiller.TryBeginGap(ref inGap, ref gapCount));
        var silence = CaptureGapFiller.CreateSilence(format, 200);
        frames.Add(new AudioFrame(silence, format, 0));
        frames.Add(new AudioFrame(new byte[352 * 4], format, 1));

        Assert.Equal(1, gapCount);
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Pcm.Length > 0);
        Assert.All(frames[0].Pcm.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Gap_fill_emits_one_second_of_silence_per_second_of_gap()
    {
        // Mirrors the WasapiLoopbackSource timer loop: fill the whole elapsed span each
        // tick so the RTP timeline advances at wall-clock rate instead of drifting.
        const long freq = 10_000_000;
        const double gapMs = 1000;
        var format = new AudioFormat(44100, 2, 16);

        var inGap = 0;
        long gapCount = 0;
        long lastEmit = 0;
        var emittedBytes = 0;

        for (var nowMs = CaptureGapFiller.ChunkMilliseconds;
             nowMs <= gapMs;
             nowMs += CaptureGapFiller.ChunkMilliseconds)
        {
            var now = (long)(nowMs / 1000.0 * freq);
            var delta = now - lastEmit;

            if (inGap == 0 && !CaptureGapFiller.IsGap(delta, freq))
            {
                continue;
            }

            CaptureGapFiller.TryBeginGap(ref inGap, ref gapCount);
            emittedBytes += CaptureGapFiller
                .CreateSilence(format, CaptureGapFiller.GapMilliseconds(delta, freq))
                .Length;
            lastEmit = now;
        }

        Assert.Equal(1, gapCount);
        var emittedMs = emittedBytes / (double)format.BlockAlign / format.SampleRate * 1000.0;
        // One timer tick of slack for the trailing partial interval.
        Assert.InRange(emittedMs, gapMs - CaptureGapFiller.ChunkMilliseconds, gapMs);
    }

    [Fact]
    public void CreateBurstSilence_splits_gap_into_chunk_sized_frames_summing_to_total()
    {
        var format = new AudioFormat(44100, 2, 16);
        const double gapMs = 55; // not an exact multiple of ChunkMilliseconds
        var chunks = CaptureGapFiller.CreateBurstSilence(format, gapMs);

        // 55ms / 10ms chunks => 5 full chunks + 1 partial (5ms) chunk.
        Assert.Equal(6, chunks.Count);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            Assert.Equal(
                CaptureGapFiller.CreateSilence(format, CaptureGapFiller.ChunkMilliseconds).Length,
                chunks[i].Length);
        }

        // Each chunk rounds its own frame count, so the burst total can differ from a
        // single-shot CreateSilence call by at most one frame's worth of rounding slack.
        var totalBytes = chunks.Sum(c => c.Length);
        var singleShotBytes = CaptureGapFiller.CreateSilence(format, gapMs).Length;
        Assert.InRange(totalBytes, singleShotBytes - format.BlockAlign, singleShotBytes + format.BlockAlign);
        Assert.All(chunks, chunk => Assert.All(chunk, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void CreateBurstSilence_returns_empty_for_non_positive_gap()
    {
        var format = new AudioFormat(44100, 2, 16);
        Assert.Empty(CaptureGapFiller.CreateBurstSilence(format, 0));
        Assert.Empty(CaptureGapFiller.CreateBurstSilence(format, -5));
    }

    [Fact]
    public void JitterTracker_defaults_to_max_threshold_before_any_samples()
    {
        var time = new ManualTimeProvider();
        var tracker = new CaptureGapFiller.JitterTracker(time);
        Assert.Equal(
            CaptureGapFiller.MaxAdaptiveThresholdMilliseconds,
            tracker.CurrentThresholdMilliseconds);
    }

    [Fact]
    public void JitterTracker_scales_threshold_down_for_tight_event_driven_spacing()
    {
        // Auto mode's 10ms event-driven callbacks: threshold should settle well below the
        // legacy fixed 120ms so a 48-120ms hiccup no longer slips through unfilled.
        var time = new ManualTimeProvider();
        var tracker = new CaptureGapFiller.JitterTracker(time);

        tracker.RecordCallback();
        foreach (var _ in Enumerable.Range(0, 20))
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            tracker.RecordCallback();
        }

        Assert.True(
            tracker.CurrentThresholdMilliseconds < CaptureGapFiller.ThresholdMilliseconds,
            $"Adaptive threshold {tracker.CurrentThresholdMilliseconds}ms should be tighter " +
            "than the legacy 120ms constant for 10ms event-driven spacing.");
        Assert.True(tracker.CurrentThresholdMilliseconds >= 20 &&
            tracker.CurrentThresholdMilliseconds <= 40);
    }

    [Fact]
    public void JitterTracker_scales_threshold_up_for_looser_polling_spacing()
    {
        // Legacy 50ms-interval polling capture: healthy callbacks land ~25-62.5ms apart, so
        // the adaptive threshold must clear that cadence to avoid phantom gaps.
        var time = new ManualTimeProvider();
        var tracker = new CaptureGapFiller.JitterTracker(time);

        tracker.RecordCallback();
        foreach (var callbackMs in new[] { 25.0, 31.25, 50.0, 62.5, 50.0, 31.25 })
        {
            time.Advance(TimeSpan.FromMilliseconds(callbackMs));
            tracker.RecordCallback();
        }

        Assert.True(
            tracker.CurrentThresholdMilliseconds > 62.5,
            $"Adaptive threshold {tracker.CurrentThresholdMilliseconds}ms must clear the " +
            "62.5ms worst-case polling callback interval.");
        Assert.True(
            tracker.CurrentThresholdMilliseconds <= CaptureGapFiller.MaxAdaptiveThresholdMilliseconds);
    }

    [Fact]
    public void JitterTracker_IsGapNow_detects_hiccup_missed_by_fixed_threshold()
    {
        // The motivating scenario: a 90ms hiccup during 10ms event-driven capture is well
        // under the legacy fixed 120ms threshold and would be missed entirely.
        var time = new ManualTimeProvider();
        var tracker = new CaptureGapFiller.JitterTracker(time);

        tracker.RecordCallback();
        foreach (var _ in Enumerable.Range(0, 20))
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            tracker.RecordCallback();
        }

        time.Advance(TimeSpan.FromMilliseconds(90));
        Assert.False(CaptureGapFiller.IsGap(
            deltaTicks: (long)(90.0 / 1000.0 * Stopwatch.Frequency),
            frequencyHz: Stopwatch.Frequency));
        Assert.True(tracker.IsGapNow());
    }

    [Fact]
    public void JitterTracker_does_not_let_a_gap_interval_pollute_the_jitter_window()
    {
        var time = new ManualTimeProvider();
        var tracker = new CaptureGapFiller.JitterTracker(time);

        tracker.RecordCallback();
        foreach (var _ in Enumerable.Range(0, 10))
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            tracker.RecordCallback();
        }

        var thresholdBeforeGap = tracker.CurrentThresholdMilliseconds;

        // A genuine gap: far larger than the healthy spacing recorded so far.
        time.Advance(TimeSpan.FromMilliseconds(500));
        tracker.RecordCallback();

        Assert.Equal(thresholdBeforeGap, tracker.CurrentThresholdMilliseconds);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
