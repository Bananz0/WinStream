using WinStream.Core.Audio;

namespace WinStream.Tests;

public class RmsCalculatorTests
{
    [Fact]
    public void CalculatePcm16_ReturnsZeroForEmptyBuffer()
    {
        Assert.Equal(0, RmsCalculator.CalculatePcm16(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void CalculatePcm16_DetectsNonSilentSine()
    {
        var frame = CreatePcm16Sine(amplitude: 0.5);
        var rms = RmsCalculator.CalculatePcm16(frame);
        Assert.True(rms > RmsCalculator.DefaultSilenceThreshold);
        Assert.False(RmsCalculator.IsSilent(rms));
    }

    [Fact]
    public void CalculatePcm16_DetectsSilence()
    {
        var silent = new byte[256];
        var rms = RmsCalculator.CalculatePcm16(silent);
        Assert.True(RmsCalculator.IsSilent(rms));
    }

    private static byte[] CreatePcm16Sine(double amplitude)
    {
        const int sampleRate = 44100;
        const int frames = 882;
        var buffer = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * amplitude * short.MaxValue);
            for (var channel = 0; channel < 2; channel++)
            {
                var offset = (i * 2 + channel) * 2;
                buffer[offset] = (byte)(sample & 0xff);
                buffer[offset + 1] = (byte)((sample >> 8) & 0xff);
            }
        }

        return buffer;
    }
}
