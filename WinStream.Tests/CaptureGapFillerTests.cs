using System.Diagnostics;
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
}
