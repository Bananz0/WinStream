using WinStream.Core.Audio;
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
}
