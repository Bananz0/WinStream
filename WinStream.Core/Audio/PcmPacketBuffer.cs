namespace WinStream.Core.Audio;

/// <summary>
/// Converts PCM to 44.1 kHz stereo 16-bit and emits fixed 352-frame RAOP packets.
/// Non-44.1 sources use linear interpolation (with one-frame hold across pushes).
/// </summary>
public sealed class PcmPacketBuffer
{
    private readonly byte[] _packet = new byte[AlacPacketBytes];
    private int _filled;
    private double _resampleCursor;
    private short _holdLeft;
    private short _holdRight;
    private bool _hasHold;

    private const int TargetRate = 44100;
    private const int TargetChannels = 2;
    private const int FramesPerPacket = AudioPacingConstants.PacketFrames;
    private const int AlacPacketBytes = Protocol.Raop.AlacEncoder.PcmBytesPerPacket;

    /// <summary>
    /// Conversion policy. In v1, <see cref="AudioFidelity.HighFidelity"/> matches
    /// <see cref="AudioFidelity.Auto"/> (direct append at 44.1 stereo; linear when converting).
    /// <see cref="AudioFidelity.Standard"/> also uses linear when converting — no HQ SRC path yet.
    /// </summary>
    public AudioFidelity Fidelity { get; set; } = AudioFidelity.Auto;

    public IEnumerable<byte[]> Push(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        var packets = new List<byte[]>();
        Push(pcm, format, packets);
        return packets;
    }

    /// <summary>Appends completed packets into <paramref name="packets"/> (caller clears).</summary>
    public void Push(ReadOnlySpan<byte> pcm, AudioFormat format, List<byte[]> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        if (format.BitsPerSample != 16)
        {
            throw new NotSupportedException("Only 16-bit PCM is supported.");
        }

        if (format.Channels is < 1 or > 8)
        {
            throw new NotSupportedException("Unsupported channel count.");
        }

        var sourceFrames = pcm.Length / (format.Channels * 2);
        if (sourceFrames <= 0)
        {
            return;
        }

        if (format.SampleRate == TargetRate && format.Channels == TargetChannels)
        {
            AppendDirect(pcm, packets);
            return;
        }

        // Rate/channel conversion: always linear interpolation in v1
        // (Standard "forces linear"; Auto/HighFidelity share the same converter).
        _ = Fidelity; // reserved for a future HQ SRC path
        var ratio = (double)format.SampleRate / TargetRate;
        while (true)
        {
            var index = (int)Math.Floor(_resampleCursor);
            var frac = _resampleCursor - index;
            var next = index + 1;

            if (next >= sourceFrames)
            {
                break;
            }

            if (index < -1 || (index < 0 && !_hasHold))
            {
                break;
            }

            ReadStereoPair(pcm, format, index, out var left0, out var right0);
            ReadStereoPair(pcm, format, next, out var left1, out var right1);
            WriteInterpolated(left0, right0, left1, right1, frac);
            _filled += 4;
            if (_filled == AlacPacketBytes)
            {
                packets.Add(_packet.ToArray());
                _filled = 0;
            }

            _resampleCursor += ratio;
        }

        ReadStereoPair(pcm, format, sourceFrames - 1, out _holdLeft, out _holdRight);
        _hasHold = true;
        _resampleCursor -= sourceFrames;
        if (_resampleCursor < -1)
        {
            _resampleCursor = -1;
        }
    }

    /// <summary>
    /// Approximate 44.1 kHz stereo frames that <see cref="Push"/> will emit for
    /// this buffer. Used by the fan-out clock so shared timestamps track RTP.
    /// </summary>
    public static uint EstimateOutputFrames(int pcmByteLength, AudioFormat format)
    {
        if (format.Channels <= 0 || format.BitsPerSample <= 0)
        {
            return 0;
        }

        var bytesPerFrame = format.Channels * (format.BitsPerSample / 8);
        if (bytesPerFrame <= 0)
        {
            return 0;
        }

        var sourceFrames = (uint)(pcmByteLength / bytesPerFrame);
        if (sourceFrames == 0)
        {
            return 0;
        }

        if (format.SampleRate == TargetRate)
        {
            return sourceFrames;
        }

        return (uint)Math.Max(
            1,
            sourceFrames * (long)TargetRate / Math.Max(1, format.SampleRate));
    }

    public void Reset()
    {
        _filled = 0;
        _resampleCursor = 0;
        _hasHold = false;
        _holdLeft = 0;
        _holdRight = 0;
    }

    private void AppendDirect(ReadOnlySpan<byte> pcm, List<byte[]> packets)
    {
        var offset = 0;
        while (offset < pcm.Length)
        {
            var copy = Math.Min(AlacPacketBytes - _filled, pcm.Length - offset);
            pcm.Slice(offset, copy).CopyTo(_packet.AsSpan(_filled));
            _filled += copy;
            offset += copy;
            if (_filled == AlacPacketBytes)
            {
                packets.Add(_packet.ToArray());
                _filled = 0;
            }
        }
    }

    private void WriteInterpolated(
        short left0,
        short right0,
        short left1,
        short right1,
        double frac)
    {
        var left = Lerp(left0, left1, frac);
        var right = Lerp(right0, right1, frac);
        _packet[_filled] = (byte)(left & 0xff);
        _packet[_filled + 1] = (byte)((left >> 8) & 0xff);
        _packet[_filled + 2] = (byte)(right & 0xff);
        _packet[_filled + 3] = (byte)((right >> 8) & 0xff);
    }

    private void ReadStereoPair(
        ReadOnlySpan<byte> pcm,
        AudioFormat format,
        int frameIndex,
        out short left,
        out short right)
    {
        if (frameIndex < 0)
        {
            left = _holdLeft;
            right = _holdRight;
            return;
        }

        var frameOffset = frameIndex * format.Channels * 2;
        if (format.Channels == 1)
        {
            left = right = ReadSample(pcm, frameOffset);
            return;
        }

        left = ReadSample(pcm, frameOffset);
        right = ReadSample(pcm, frameOffset + 2);
    }

    private static short Lerp(short a, short b, double frac)
    {
        var mixed = a + ((b - a) * frac);
        if (mixed >= short.MaxValue)
        {
            return short.MaxValue;
        }

        if (mixed <= short.MinValue)
        {
            return short.MinValue;
        }

        return (short)Math.Round(mixed, MidpointRounding.AwayFromZero);
    }

    private static short ReadSample(ReadOnlySpan<byte> pcm, int offset) =>
        (short)(pcm[offset] | (pcm[offset + 1] << 8));
}
