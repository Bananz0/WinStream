using System.Buffers.Binary;

namespace WinStream.Core.Protocol.Raop;

public static class RtpPacketizer
{
    public const byte PayloadAudio = 0x60;
    public const byte PayloadTimingRequest = 0x52;
    public const byte PayloadTimingResponse = 0x53;
    public const byte PayloadSync = 0x54;
    public const byte PayloadResendRequest = 0x55;

    public static int WriteAudioPacket(
        Span<byte> destination,
        ushort sequenceNumber,
        uint timestamp,
        uint ssrc,
        ReadOnlySpan<byte> payload,
        bool marker)
    {
        const int headerLength = 12;
        if (destination.Length < headerLength + payload.Length)
        {
            throw new ArgumentException("Destination is too small for RTP audio packet.");
        }

        destination[0] = 0x80;
        destination[1] = (byte)(PayloadAudio | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], ssrc);
        payload.CopyTo(destination[headerLength..]);
        return headerLength + payload.Length;
    }

    public static int WriteSyncPacket(
        Span<byte> destination,
        uint nowMinusLatency,
        ulong ntpTimestamp,
        uint now,
        bool first)
    {
        if (destination.Length < 20)
        {
            throw new ArgumentException("Destination is too small for sync packet.");
        }

        destination[0] = (byte)(0x80 | (first ? 0x10 : 0));
        destination[1] = (byte)(PayloadSync | 0x80);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 7);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], nowMinusLatency);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], (uint)(ntpTimestamp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], (uint)ntpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], now);
        return 20;
    }

    public static int WriteTimingResponse(
        Span<byte> destination,
        ushort sequenceNumber,
        ulong referenceNtp,
        ulong receivedNtp,
        ulong sendNtp)
    {
        if (destination.Length < 32)
        {
            throw new ArgumentException("Destination is too small for timing response.");
        }

        destination[0] = 0x80;
        destination[1] = (byte)(PayloadTimingResponse | 0x80);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], (uint)(referenceNtp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], (uint)referenceNtp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], (uint)(receivedNtp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[20..], (uint)receivedNtp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], (uint)(sendNtp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[28..], (uint)sendNtp);
        return 32;
    }

    public static bool TryReadTimingRequest(
        ReadOnlySpan<byte> packet,
        out ushort sequenceNumber,
        out ulong sendNtp)
    {
        sequenceNumber = 0;
        sendNtp = 0;
        if (packet.Length < 32)
        {
            return false;
        }

        var payloadType = (byte)(packet[1] & 0x7f);
        if (payloadType != PayloadTimingRequest)
        {
            return false;
        }

        sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(packet[24..]);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(packet[28..]);
        sendNtp = ((ulong)seconds << 32) | fraction;
        return true;
    }

    public static bool TryReadResendRequest(
        ReadOnlySpan<byte> packet,
        out ushort missedSequence,
        out ushort count)
    {
        missedSequence = 0;
        count = 0;
        if (packet.Length < 8)
        {
            return false;
        }

        var payloadType = (byte)(packet[1] & 0x7f);
        if (payloadType != PayloadResendRequest)
        {
            return false;
        }

        missedSequence = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        count = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        return true;
    }
}
