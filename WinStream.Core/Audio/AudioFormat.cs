namespace WinStream.Core.Audio;

public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int BytesPerSample => BitsPerSample / 8;

    public int BlockAlign => BytesPerSample * Channels;

    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit";
}
