using System.Buffers.Binary;
using WinStream.Core.Audio;

namespace WinStream.Core.Protocol.Link;

public static class Wsl1Packet
{
    public static int GetPacketSize(int samplesPerChannel, int channels) =>
        Wsl1Constants.HeaderSize + samplesPerChannel * channels * sizeof(short);

    public static int Write(
        ReadOnlySpan<byte> pcmPayload,
        AudioFormat format,
        ushort sequence,
        long txQpcTicks,
        byte flags,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.BitsPerSample != 16)
        {
            throw new ArgumentException("WSL1 v1 supports PCM S16LE only.", nameof(format));
        }

        var samplesPerChannel = pcmPayload.Length / format.BlockAlign;
        if (samplesPerChannel <= 0 || pcmPayload.Length != samplesPerChannel * format.BlockAlign)
        {
            throw new ArgumentException("PCM payload must align to full frames.", nameof(pcmPayload));
        }

        var required = GetPacketSize(samplesPerChannel, format.Channels);
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination needs at least {required} bytes.",
                nameof(destination));
        }

        Wsl1Constants.Magic.CopyTo(destination);
        var offset = 4;
        destination[offset++] = Wsl1Constants.Version;
        destination[offset++] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], sequence);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], (uint)samplesPerChannel);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], (uint)format.SampleRate);
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)format.Channels);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], Wsl1Constants.FormatS16Le);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], txQpcTicks);
        offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], 0);
        pcmPayload.CopyTo(destination[Wsl1Constants.HeaderSize..]);
        return required;
    }

    public static bool TryRead(
        ReadOnlySpan<byte> packet,
        out Wsl1PacketHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (packet.Length < Wsl1Constants.HeaderSize)
        {
            return false;
        }

        if (!packet.StartsWith(Wsl1Constants.Magic))
        {
            return false;
        }

        var version = packet[4];
        if (version != Wsl1Constants.Version)
        {
            return false;
        }

        var flags = packet[5];
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        var samplesPerChannel = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(packet[16..]);
        var format = BinaryPrimitives.ReadUInt16LittleEndian(packet[18..]);
        var txQpcTicks = BinaryPrimitives.ReadInt64LittleEndian(packet[20..]);
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(packet[28..]);

        if (format != Wsl1Constants.FormatS16Le || channels == 0 || samplesPerChannel == 0)
        {
            return false;
        }

        var payloadBytes = checked((int)(samplesPerChannel * channels * sizeof(short)));
        if (packet.Length < Wsl1Constants.HeaderSize + payloadBytes)
        {
            return false;
        }

        header = new Wsl1PacketHeader(
            version,
            flags,
            sequence,
            samplesPerChannel,
            sampleRate,
            channels,
            format,
            txQpcTicks,
            reserved);
        payload = packet[Wsl1Constants.HeaderSize..(Wsl1Constants.HeaderSize + payloadBytes)];
        return true;
    }
}
