namespace WinStream.Core.Audio;

/// <summary>
/// Converts PCM to 44.1 kHz stereo 16-bit and emits fixed 352-frame RAOP packets.
/// </summary>
public sealed class PcmPacketBuffer
{
    private readonly byte[] _packet = new byte[AlacPacketBytes];
    private int _filled;
    private double _resampleCursor;

    private const int TargetRate = 44100;
    private const int TargetChannels = 2;
    private const int FramesPerPacket = Protocol.Raop.AlacEncoder.FramesPerPacket;
    private const int AlacPacketBytes = Protocol.Raop.AlacEncoder.PcmBytesPerPacket;

    public IEnumerable<byte[]> Push(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        if (format.BitsPerSample != 16)
        {
            throw new NotSupportedException("Only 16-bit PCM is supported.");
        }

        if (format.Channels is < 1 or > 8)
        {
            throw new NotSupportedException("Unsupported channel count.");
        }

        var packets = new List<byte[]>();
        var sourceFrames = pcm.Length / (format.Channels * 2);
        if (sourceFrames <= 0)
        {
            return packets;
        }

        if (format.SampleRate == TargetRate && format.Channels == TargetChannels)
        {
            AppendDirect(pcm, packets);
            return packets;
        }

        // Linear resample + down/up-mix to stereo.
        var ratio = (double)format.SampleRate / TargetRate;
        while (_resampleCursor < sourceFrames)
        {
            var index = (int)_resampleCursor;
            if (index >= sourceFrames)
            {
                break;
            }

            WriteStereoFrame(pcm, format, index);
            _filled += 4;
            if (_filled == AlacPacketBytes)
            {
                packets.Add(_packet.ToArray());
                _filled = 0;
            }

            _resampleCursor += ratio;
        }

        _resampleCursor -= sourceFrames;
        if (_resampleCursor < 0)
        {
            _resampleCursor = 0;
        }

        return packets;
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

    private void WriteStereoFrame(ReadOnlySpan<byte> pcm, AudioFormat format, int frameIndex)
    {
        var frameOffset = frameIndex * format.Channels * 2;
        short left;
        short right;
        if (format.Channels == 1)
        {
            left = right = ReadSample(pcm, frameOffset);
        }
        else
        {
            left = ReadSample(pcm, frameOffset);
            right = ReadSample(pcm, frameOffset + 2);
        }

        _packet[_filled] = (byte)(left & 0xff);
        _packet[_filled + 1] = (byte)((left >> 8) & 0xff);
        _packet[_filled + 2] = (byte)(right & 0xff);
        _packet[_filled + 3] = (byte)((right >> 8) & 0xff);
    }

    private static short ReadSample(ReadOnlySpan<byte> pcm, int offset) =>
        (short)(pcm[offset] | (pcm[offset + 1] << 8));
}
