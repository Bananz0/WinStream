using System.Buffers.Binary;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RtpPacketizerTests
{
    [Fact]
    public void WriteAudioPacket_sets_marker_bit_when_requested()
    {
        var destination = new byte[16];
        var payload = new byte[] { 1, 2, 3, 4 };

        var length = RtpPacketizer.WriteAudioPacket(
            destination,
            sequenceNumber: 0x1234,
            timestamp: 0x89abcdef,
            ssrc: 0x11223344,
            payload,
            marker: true);

        Assert.Equal(16, length);
        Assert.Equal(0x80, destination[0]);
        Assert.Equal(0xe0, destination[1]); // payload 0x60 | marker 0x80
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(destination.AsSpan(2)));
        Assert.Equal(0x89abcdefu, BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(8)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, destination[12..]);
    }

    [Fact]
    public void WriteAudioPacket_without_marker_uses_payload_type_only()
    {
        var destination = new byte[12];
        var length = RtpPacketizer.WriteAudioPacket(
            destination,
            1,
            2,
            3,
            ReadOnlySpan<byte>.Empty,
            marker: false);

        Assert.Equal(12, length);
        Assert.Equal(0x60, destination[1]);
    }

    [Fact]
    public void TryReadTimingRequest_parses_send_ntp()
    {
        var packet = new byte[32];
        packet[0] = 0x80;
        packet[1] = 0xd2; // marker + 0x52
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 9);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24), 0x11111111);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28), 0x22222222);

        var ok = RtpPacketizer.TryReadTimingRequest(packet, out var seq, out var ntp);

        Assert.True(ok);
        Assert.Equal(9, seq);
        Assert.Equal(0x1111111122222222UL, ntp);
    }
}
