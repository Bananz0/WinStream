namespace WinStream.Core.Audio;

/// <summary>
/// PCM packet pacing constants shared by capture, send scheduling, and protocol encode.
/// </summary>
public static class AudioPacingConstants
{
    /// <summary>ALAC/RTP frames per packet at 44.1 kHz.</summary>
    public const int PacketFrames = 352;
}
