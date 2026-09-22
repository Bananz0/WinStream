namespace WinStream.Core.Audio;

public readonly struct AudioFrame
{
    public AudioFrame(ReadOnlyMemory<byte> pcm, AudioFormat format, long timestampTicks)
    {
        Pcm = pcm;
        Format = format;
        TimestampTicks = timestampTicks;
    }

    public ReadOnlyMemory<byte> Pcm { get; }

    public AudioFormat Format { get; }

    public long TimestampTicks { get; }
}
