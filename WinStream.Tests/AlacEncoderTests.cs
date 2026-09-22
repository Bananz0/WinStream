using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class AlacEncoderTests
{
    [Fact]
    public void Encode_writes_uncompressed_header_and_big_endian_samples()
    {
        // Two stereo frames: L=0x1122, R=0x3344 then L=0x5566, R=0x7788 (LE input)
        var pcm = new byte[]
        {
            0x22, 0x11, 0x44, 0x33,
            0x66, 0x55, 0x88, 0x77
        };
        var destination = new byte[AlacEncoder.GetMaxEncodedLength(pcm.Length)];

        var written = AlacEncoder.Encode(pcm, destination);

        // 23-bit ALAC header is not byte-aligned; samples continue in the same byte.
        Assert.Equal(11, written);
        Assert.Equal(0b0010_0000, destination[0]);
        Assert.Equal(0b0000_0000, destination[1]);
        Assert.Equal(0b0000_0010, destination[2]); // header ends with is-not-compressed at bit 1
        // Remaining bits are big-endian PCM shifted by the partial header byte.
        Assert.NotEqual(pcm, destination.AsSpan(3, pcm.Length).ToArray());
        Assert.Equal(0x22, destination[3]); // 0x11 << 1 from bit-unaligned writer
    }

    [Fact]
    public void Encode_rejects_odd_frame_size()
    {
        var pcm = new byte[3];
        var destination = new byte[16];
        Assert.Throws<ArgumentException>(() => AlacEncoder.Encode(pcm, destination));
    }
}
