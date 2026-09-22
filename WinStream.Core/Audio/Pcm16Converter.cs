using System.Buffers.Binary;

namespace WinStream.Core.Audio;

/// <summary>Sample layout a capture endpoint hands us before normalization.</summary>
public enum CaptureSampleFormat
{
    Pcm16,
    Pcm24,
    Pcm32,
    Float32,
    Float64
}

/// <summary>
/// Normalizes capture buffers to the 16-bit PCM the streaming pipeline expects.
/// WASAPI shared-mode loopback almost always delivers 32-bit float.
/// </summary>
public static class Pcm16Converter
{
    public static int BytesPerSample(CaptureSampleFormat format) => format switch
    {
        CaptureSampleFormat.Pcm16 => 2,
        CaptureSampleFormat.Pcm24 => 3,
        CaptureSampleFormat.Pcm32 or CaptureSampleFormat.Float32 => 4,
        CaptureSampleFormat.Float64 => 8,
        _ => throw new NotSupportedException($"Unsupported capture format {format}.")
    };

    public static byte[] ToPcm16(ReadOnlySpan<byte> source, CaptureSampleFormat format)
    {
        if (format == CaptureSampleFormat.Pcm16)
        {
            return source.ToArray();
        }

        var stride = BytesPerSample(format);
        var sampleCount = source.Length / stride;
        var destination = new byte[sampleCount * 2];

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = source.Slice(i * stride, stride);
            BinaryPrimitives.WriteInt16LittleEndian(
                destination.AsSpan(i * 2),
                ConvertSample(sample, format));
        }

        return destination;
    }

    private static short ConvertSample(ReadOnlySpan<byte> sample, CaptureSampleFormat format) =>
        format switch
        {
            CaptureSampleFormat.Pcm24 => (short)(sample[1] | (sample[2] << 8)),
            CaptureSampleFormat.Pcm32 => (short)BinaryPrimitives.ReadInt16LittleEndian(sample[2..]),
            CaptureSampleFormat.Float32 =>
                FromUnitFloat(BinaryPrimitives.ReadSingleLittleEndian(sample)),
            CaptureSampleFormat.Float64 =>
                FromUnitFloat(BinaryPrimitives.ReadDoubleLittleEndian(sample)),
            _ => throw new NotSupportedException($"Unsupported capture format {format}.")
        };

    // Tiny LCG state for TPDF dither (avoids ThreadLocal/Random alloc on the capture path).
    private static int s_ditherState = unchecked((int)0xA5A5_5A5Au);

    private static short FromUnitFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        // Scale by 32767 so full-scale +1.0 maps cleanly, then add ±1 LSB triangular
        // dither so quiet material does not freeze into quantization grit.
        var scaled = Math.Clamp(value, -1.0, 1.0) * short.MaxValue;
        scaled += NextTriangularDither();
        if (scaled >= short.MaxValue)
        {
            return short.MaxValue;
        }

        if (scaled <= short.MinValue)
        {
            return short.MinValue;
        }

        return (short)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private static double NextTriangularDither()
    {
        // Two uniform [-0.5, 0.5) samples → triangular pdf over (-1, 1) LSB.
        return NextUnitNoise() + NextUnitNoise();
    }

    private static double NextUnitNoise()
    {
        unchecked
        {
            s_ditherState = (s_ditherState * 1664525) + 1013904223;
            // High bits → [0, 1)
            var unit = (s_ditherState >>> 8) * (1.0 / 16777216.0);
            return unit - 0.5;
        }
    }
}
