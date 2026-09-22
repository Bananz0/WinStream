using System.Buffers.Binary;
using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;

namespace WinStream.Tests;

public class Wsl1PacketTests
{
    private static readonly AudioFormat Format =
        new(Wsl1Constants.DefaultSampleRate, Wsl1Constants.DefaultChannels, 16);

    [Fact]
    public void Header_field_offsets_match_wire_lock()
    {
        var pcm = CreatePayload(Wsl1Constants.DefaultSamplesPerChannel);
        var packet = new byte[Wsl1Constants.DefaultPacketSize];
        Wsl1Packet.Write(pcm, Format, sequence: 42, txQpcTicks: 0x0123_4567_89AB_CDEF, flags: 0, packet);

        Assert.Equal("WSL1"u8.ToArray(), packet.AsSpan(0, 4).ToArray());
        Assert.Equal(Wsl1Constants.Version, packet[4]);
        Assert.Equal(0, packet[5]);
        Assert.Equal((ushort)42, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2)));
        Assert.Equal(
            (uint)Wsl1Constants.DefaultSamplesPerChannel,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)));
        Assert.Equal(
            (uint)Wsl1Constants.DefaultSampleRate,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)));
        Assert.Equal(
            (ushort)Wsl1Constants.DefaultChannels,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(16, 2)));
        Assert.Equal(
            Wsl1Constants.FormatS16Le,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(18, 2)));
        Assert.Equal(
            0x0123_4567_89AB_CDEF,
            BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(20, 8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(28, 4)));
        Assert.Equal(pcm, packet.AsSpan(Wsl1Constants.HeaderSize).ToArray());
    }

    [Fact]
    public void Default_payload_is_384_bytes()
    {
        Assert.Equal(384, Wsl1Constants.DefaultPayloadBytes);
        Assert.Equal(416, Wsl1Constants.DefaultPacketSize);
    }

    [Fact]
    public void TryRead_round_trips_Write()
    {
        var pcm = CreatePayload(Wsl1Constants.DefaultSamplesPerChannel);
        var packet = new byte[Wsl1Constants.DefaultPacketSize];
        Wsl1Packet.Write(pcm, Format, 7, 999, 0, packet);

        Assert.True(Wsl1Packet.TryRead(packet, out var header, out var payload));
        Assert.Equal(7, header.Sequence);
        Assert.Equal(999, header.TxQpcTicks);
        Assert.Equal(Wsl1Constants.DefaultSampleRate, (int)header.SampleRate);
        Assert.Equal(pcm, payload.ToArray());
    }

    [Fact]
    public void TryRead_rejects_bad_magic()
    {
        var packet = new byte[Wsl1Constants.DefaultPacketSize];
        Assert.False(Wsl1Packet.TryRead(packet, out _, out _));
    }

    [Fact]
    public void TryRead_rejects_bad_version_and_truncated_payload()
    {
        var pcm = CreatePayload(Wsl1Constants.DefaultSamplesPerChannel);
        var packet = new byte[Wsl1Constants.DefaultPacketSize];
        Wsl1Packet.Write(pcm, Format, 1, 2, 3, packet);

        packet[4] = 2;
        Assert.False(Wsl1Packet.TryRead(packet, out _, out _));

        packet[4] = Wsl1Constants.Version;
        Assert.False(Wsl1Packet.TryRead(packet.AsSpan(0, packet.Length - 1), out _, out _));
    }

    [Fact]
    public void Write_rejects_misaligned_pcm_and_small_destination()
    {
        Assert.Throws<ArgumentException>(() =>
            Wsl1Packet.Write(new byte[3], Format, 0, 0, 0, new byte[64]));

        var pcm = CreatePayload(Wsl1Constants.DefaultSamplesPerChannel);
        Assert.Throws<ArgumentException>(() =>
            Wsl1Packet.Write(pcm, Format, 0, 0, 0, new byte[32]));
    }

    private static byte[] CreatePayload(int samplesPerChannel)
    {
        var bytes = samplesPerChannel * Format.BlockAlign;
        var pcm = new byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            pcm[i] = (byte)(i & 0xFF);
        }

        return pcm;
    }
}
