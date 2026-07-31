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

    [Fact]
    public void WriteSyncPacket_sets_extension_on_first()
    {
        var destination = new byte[20];
        var length = RtpPacketizer.WriteSyncPacket(
            destination,
            nowMinusLatency: 100,
            ntpTimestamp: 0x0102030405060708UL,
            now: 200,
            first: true);

        Assert.Equal(20, length);
        Assert.Equal(0x90, destination[0]);
        Assert.Equal(0xd4, destination[1]);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(4)));
        Assert.Equal(200u, BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(16)));
    }

    [Fact]
    public void WriteTimeAnnouncePacket_carries_grandmaster_identity()
    {
        var destination = new byte[28];

        var length = RtpPacketizer.WriteTimeAnnouncePacket(
            destination,
            anchorRtpTime: 1822257130,
            networkTimeNanoseconds: 538506250528900UL,
            applyRtpTime: 1822345330,
            clockId: 0x001FF3A0F3B30008UL,
            first: true);

        Assert.Equal(28, length);
        Assert.Equal(0x90, destination[0]); // sentinel bit set on the first anchor
        Assert.Equal(0xd7, destination[1]);
        Assert.Equal(1822257130u, BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(4)));
        Assert.Equal(
            538506250528900UL,
            BinaryPrimitives.ReadUInt64BigEndian(destination.AsSpan(8)));
        Assert.Equal(1822345330u, BinaryPrimitives.ReadUInt32BigEndian(destination.AsSpan(16)));
        Assert.Equal(
            0x001FF3A0F3B30008UL,
            BinaryPrimitives.ReadUInt64BigEndian(destination.AsSpan(20)));
    }

    [Fact]
    public void WriteTimeAnnouncePacket_clears_sentinel_after_the_first()
    {
        var destination = new byte[28];

        RtpPacketizer.WriteTimeAnnouncePacket(destination, 1, 2, 3, 4, first: false);

        Assert.Equal(0x80, destination[0]);
    }

    [Fact]
    public void WriteTimeAnnouncePacket_writes_sequence_seven()
    {
        var destination = new byte[28];
        RtpPacketizer.WriteTimeAnnouncePacket(destination, 1, 2, 3, 4, first: false);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(destination.AsSpan(2)));
    }

    [Fact]
    public void WriteTimeAnnouncePacket_rejects_short_destination()
    {
        Assert.Throws<ArgumentException>(() =>
            RtpPacketizer.WriteTimeAnnouncePacket(new byte[20], 1, 2, 3, 4, first: true));
    }

    [Fact]
    public void TryReadResendRequest_parses_range()
    {
        var packet = new byte[8];
        packet[0] = 0x80;
        packet[1] = 0x55;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 3);

        Assert.True(RtpPacketizer.TryReadResendRequest(packet, out var missed, out var count));
        Assert.Equal(12, missed);
        Assert.Equal(3, count);
    }
}
