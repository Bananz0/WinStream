namespace WinStream.Core.Protocol.Link;

/// <summary>WinStream Link v1 media wire constants.</summary>
public static class Wsl1Constants
{
    public const int HeaderSize = 32;
    public const byte Version = 1;
    public const ushort FormatS16Le = 1;
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 2;
    public const int DefaultSamplesPerChannel = 96;
    public const int DefaultPayloadBytes =
        DefaultSamplesPerChannel * DefaultChannels * sizeof(short);
    public const int DefaultPacketSize = HeaderSize + DefaultPayloadBytes;
    public const int DefaultMediaPort = LinkDefaults.MediaPort;

    public static ReadOnlySpan<byte> Magic => "WSL1"u8;
}
