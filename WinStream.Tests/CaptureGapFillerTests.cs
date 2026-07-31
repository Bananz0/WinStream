using WinStream.Core.Audio;
using System.Diagnostics;

namespace WinStream.Tests;

public class CaptureGapFillerTests
{
    [Fact]
    public void IsGap_detects_threshold()
    {
        const long freq = 10_000_000; // 100 ns ticks
        var under = (long)(40.0 / 1000.0 * freq);
        var over = (long)(60.0 / 1000.0 * freq);
        Assert.False(CaptureGapFiller.IsGap(under, freq));
        Assert.True(CaptureGapFiller.IsGap(over, freq));
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
    public void Synthetic_gap_insert_increments_discontinuity_model()
    {
        // Models WasapiLoopbackSource: gap → increment + emit silence before audio.
        long gapCount = 0;
        var format = new AudioFormat(44100, 2, 16);
        var frames = new List<AudioFrame>();

        void OnGap(double gapMs)
        {
            gapCount++;
            var silence = CaptureGapFiller.CreateSilence(format, gapMs);
            frames.Add(new AudioFrame(silence, format, 0));
        }

        void OnAudio(byte[] pcm)
        {
            frames.Add(new AudioFrame(pcm, format, 1));
        }

        Assert.True(CaptureGapFiller.IsGap(
            deltaTicks: (long)(80.0 / 1000.0 * Stopwatch.Frequency),
            frequencyHz: Stopwatch.Frequency));
        OnGap(80);
        OnAudio(new byte[352 * 4]);

        Assert.Equal(1, gapCount);
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Pcm.Length > 0);
        Assert.All(frames[0].Pcm.ToArray(), b => Assert.Equal(0, b));
    }
}
