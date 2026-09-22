namespace WinStream.Core.Protocol.Link;

public readonly struct Wsl1PacketHeader
{
    public Wsl1PacketHeader(
        byte version,
        byte flags,
        ushort sequence,
        uint samplesPerChannel,
        uint sampleRate,
        ushort channels,
        ushort format,
        long txQpcTicks,
        uint reserved)
    {
        Version = version;
        Flags = flags;
        Sequence = sequence;
        SamplesPerChannel = samplesPerChannel;
        SampleRate = sampleRate;
        Channels = channels;
        Format = format;
        TxQpcTicks = txQpcTicks;
        Reserved = reserved;
    }

    public byte Version { get; }
    public byte Flags { get; }
    public ushort Sequence { get; }
    public uint SamplesPerChannel { get; }
    public uint SampleRate { get; }
    public ushort Channels { get; }
    public ushort Format { get; }
    public long TxQpcTicks { get; }
    public uint Reserved { get; }

    public int PayloadBytes =>
        checked((int)(SamplesPerChannel * Channels * sizeof(short)));
}
