using System.Buffers.Binary;
using WinStream.Core.Audio;

namespace WinStream.Tests;

public class Pcm16ConverterTests
{
    [Fact]
    public void Float32_maps_full_scale_and_silence()
    {
        var source = FloatBytes(0f, 1f, -1f, 0.5f);

        var pcm = Pcm16Converter.ToPcm16(source, CaptureSampleFormat.Float32);

        Assert.Equal(8, pcm.Length);
        // TPDF dither is ±1 LSB around the quantized value.
        Assert.InRange(ReadSample(pcm, 0), -1, 1);
        Assert.InRange(ReadSample(pcm, 1), short.MaxValue - 1, short.MaxValue);
        Assert.InRange(ReadSample(pcm, 2), -short.MaxValue, -short.MaxValue + 1);
        Assert.InRange(ReadSample(pcm, 3), 16383, 16385);
    }

    [Fact]
    public void Float32_clamps_out_of_range_samples()
    {
        var source = FloatBytes(2.5f, -3.75f, float.NaN);

        var pcm = Pcm16Converter.ToPcm16(source, CaptureSampleFormat.Float32);

        Assert.Equal(short.MaxValue, ReadSample(pcm, 0));
        Assert.InRange(ReadSample(pcm, 1), -short.MaxValue, -short.MaxValue + 1);
        Assert.Equal(0, ReadSample(pcm, 2));
    }

    [Fact]
    public void Pcm16_passes_through_unchanged()
    {
        var source = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var pcm = Pcm16Converter.ToPcm16(source, CaptureSampleFormat.Pcm16);

        Assert.Equal(source, pcm);
    }

    [Fact]
    public void Pcm32_keeps_the_high_16_bits()
    {
        var source = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(source, int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(4), int.MinValue);

        var pcm = Pcm16Converter.ToPcm16(source, CaptureSampleFormat.Pcm32);

        Assert.Equal(short.MaxValue, ReadSample(pcm, 0));
        Assert.Equal(short.MinValue, ReadSample(pcm, 1));
    }

    [Fact]
    public void Pcm24_keeps_the_high_16_bits()
    {
        // Little-endian 24-bit 0x7FFFFF (max) and 0x800000 (min).
        var source = new byte[] { 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80 };

        var pcm = Pcm16Converter.ToPcm16(source, CaptureSampleFormat.Pcm24);

        Assert.Equal(short.MaxValue, ReadSample(pcm, 0));
        Assert.Equal(short.MinValue, ReadSample(pcm, 1));
    }

    private static byte[] FloatBytes(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static short ReadSample(byte[] pcm, int index) =>
        BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2));
}
