using WinStream.Core.Audio;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class PcmPacketBufferTests
{
    [Fact]
    public void Push_emits_352_frame_packets_for_44100_stereo()
    {
        var buffer = new PcmPacketBuffer();
        var format = new AudioFormat(44100, 2, 16);
        var onePacket = new byte[AlacEncoder.PcmBytesPerPacket];
        for (var i = 0; i < onePacket.Length; i++)
        {
            onePacket[i] = (byte)(i & 0xff);
        }

        var packets = buffer.Push(onePacket, format).ToList();

        Assert.Single(packets);
        Assert.Equal(AlacEncoder.PcmBytesPerPacket, packets[0].Length);
        Assert.Equal(onePacket, packets[0]);
    }

    [Fact]
    public void Push_buffers_partial_packets()
    {
        var buffer = new PcmPacketBuffer();
        var format = new AudioFormat(44100, 2, 16);
        var half = new byte[AlacEncoder.PcmBytesPerPacket / 2];

        Assert.Empty(buffer.Push(half, format));
        var packets = buffer.Push(half, format).ToList();
        Assert.Single(packets);
    }

    [Fact]
    public void Push_44100_stereo_skips_resample_path()
    {
        var buffer = new PcmPacketBuffer();
        var format = new AudioFormat(44100, 2, 16);
        var pcm = new byte[AlacEncoder.PcmBytesPerPacket * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i & 0xff);
        }

        var packets = buffer.Push(pcm, format).ToList();
        Assert.Equal(2, packets.Count);
        Assert.Equal(pcm.AsSpan(0, AlacEncoder.PcmBytesPerPacket).ToArray(), packets[0]);
    }

    [Fact]
    public void Push_48000_to_44100_emits_near_EstimateOutputFrames()
    {
        var buffer = new PcmPacketBuffer();
        var format = new AudioFormat(48000, 2, 16);
        // 480 frames @ 48 kHz ≈ 10 ms → ~441 frames @ 44.1 kHz.
        const int sourceFrames = 480;
        var pcm = new byte[sourceFrames * 4];
        for (var i = 0; i < sourceFrames; i++)
        {
            short sample = (short)(i * 40);
            pcm[i * 4] = (byte)(sample & 0xff);
            pcm[(i * 4) + 1] = (byte)((sample >> 8) & 0xff);
            pcm[(i * 4) + 2] = pcm[i * 4];
            pcm[(i * 4) + 3] = pcm[(i * 4) + 1];
        }

        var expected = PcmPacketBuffer.EstimateOutputFrames(pcm.Length, format);
        var packets = buffer.Push(pcm, format).ToList();
        var emittedFrames = (uint)(packets.Sum(p => p.Length) / 4);
        // Incomplete trailing packet stays in the buffer (< 352 frames).
        Assert.True(emittedFrames <= expected);
        Assert.True(expected - emittedFrames < AlacEncoder.FramesPerPacket);
    }

    [Fact]
    public void Push_linear_interp_blends_adjacent_samples()
    {
        var buffer = new PcmPacketBuffer();
        // 2× upsample so odd output samples land halfway between inputs.
        var format = new AudioFormat(22050, 2, 16);
        const int sourceFrames = 200;
        var pcm = new byte[sourceFrames * 4];
        for (var i = 0; i < sourceFrames; i++)
        {
            short sample = (short)(i == 0 ? 0 : 20000);
            WriteStereo(pcm, i, sample, sample);
        }

        var packets = buffer.Push(pcm, format).ToList();
        Assert.NotEmpty(packets);
        // Output[0] at cursor 0 → 0; output[1] at cursor 0.5 → ~10000.
        Assert.Equal(0, BitConverter.ToInt16(packets[0], 0));
        var mid = BitConverter.ToInt16(packets[0], 4);
        Assert.InRange(mid, 9000, 11000);
    }

    [Fact]
    public void Push_44100_stereo_Fidelity_Auto_uses_direct_append()
    {
        var buffer = new PcmPacketBuffer { Fidelity = AudioFidelity.Auto };
        var format = new AudioFormat(44100, 2, 16);
        var pcm = new byte[AlacEncoder.PcmBytesPerPacket];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i & 0xff);
        }

        var packets = buffer.Push(pcm, format).ToList();
        Assert.Single(packets);
        Assert.Equal(pcm, packets[0]);
    }

    [Fact]
    public void Push_HighFidelity_matches_Auto_conversion_for_48000()
    {
        var format = new AudioFormat(48000, 2, 16);
        const int sourceFrames = 480;
        var pcm = new byte[sourceFrames * 4];
        for (var i = 0; i < sourceFrames; i++)
        {
            short sample = (short)(i * 40);
            WriteStereo(pcm, i, sample, sample);
        }

        var auto = new PcmPacketBuffer { Fidelity = AudioFidelity.Auto };
        var hq = new PcmPacketBuffer { Fidelity = AudioFidelity.HighFidelity };
        var autoPackets = auto.Push(pcm, format).ToList();
        var hqPackets = hq.Push(pcm, format).ToList();
        Assert.Equal(autoPackets.Count, hqPackets.Count);
        for (var i = 0; i < autoPackets.Count; i++)
        {
            Assert.Equal(autoPackets[i], hqPackets[i]);
        }
    }

    [Fact]
    public void Push_Standard_uses_linear_when_converting()
    {
        var buffer = new PcmPacketBuffer { Fidelity = AudioFidelity.Standard };
        var format = new AudioFormat(22050, 2, 16);
        const int sourceFrames = 200;
        var pcm = new byte[sourceFrames * 4];
        for (var i = 0; i < sourceFrames; i++)
        {
            short sample = (short)(i == 0 ? 0 : 20000);
            WriteStereo(pcm, i, sample, sample);
        }

        var packets = buffer.Push(pcm, format).ToList();
        Assert.NotEmpty(packets);
        Assert.Equal(0, BitConverter.ToInt16(packets[0], 0));
        var mid = BitConverter.ToInt16(packets[0], 4);
        Assert.InRange(mid, 9000, 11000);
    }

    private static void WriteStereo(byte[] pcm, int frame, short left, short right)
    {
        var o = frame * 4;
        pcm[o] = (byte)(left & 0xff);
        pcm[o + 1] = (byte)((left >> 8) & 0xff);
        pcm[o + 2] = (byte)(right & 0xff);
        pcm[o + 3] = (byte)((right >> 8) & 0xff);
    }
}
