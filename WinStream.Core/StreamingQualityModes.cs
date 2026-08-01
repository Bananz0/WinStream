namespace WinStream.Core;

/// <summary>How much AirPlay playout buffer to use (delay vs dropout resistance).</summary>
public enum PlaybackResponsiveness
{
    Auto = 0,
    LowDelay = 1,
    Balanced = 2,
    MostStable = 3,
    /// <summary>~500 ms fixed buffer (Very low).</summary>
    VeryLow = 4,
    /// <summary>~250 ms fixed buffer (Experimental).</summary>
    Experimental = 5,
    /// <summary>Extreme ~50 ms RealTime ask (six ALAC packets); may climb under pressure.</summary>
    LabPacket = 6
}

/// <summary>How carefully to convert capture PCM before ALAC (CPU vs conversion quality).</summary>
/// <remarks>
/// v1: all modes share one converter (direct append at 44.1 stereo; linear when converting).
/// HighFidelity is reserved for a richer SRC path later.
/// </remarks>
public enum AudioFidelity
{
    Auto = 0,
    Standard = 1,
    HighFidelity = 2
}

/// <summary>
/// Which output path WinStream drives. Lives above both Persistence and Streaming so
/// settings can store it without depending on the Link streaming namespace.
/// </summary>
public enum SinkMode
{
    AirPlay = 0,
    Link = 1
}
