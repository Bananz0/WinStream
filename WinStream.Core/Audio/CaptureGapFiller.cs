namespace WinStream.Core.Audio;

/// <summary>
/// Builds PCM16 silence to bridge WASAPI loopback gaps so RTP stays continuous.
/// </summary>
public static class CaptureGapFiller
{
    public const double ThresholdMilliseconds = 50;
    public const double MaxFillMilliseconds = 2000;
    public const double ChunkMilliseconds = 10;

    public static bool IsGap(long deltaTicks, long frequencyHz) =>
        GapMilliseconds(deltaTicks, frequencyHz) > ThresholdMilliseconds;

    public static double GapMilliseconds(long deltaTicks, long frequencyHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequencyHz, 0);
        if (deltaTicks <= 0)
        {
            return 0;
        }

        return deltaTicks * 1000.0 / frequencyHz;
    }

    /// <summary>
    /// Zeroed PCM16 covering <paramref name="gapMilliseconds"/> (capped).
    /// </summary>
    public static byte[] CreateSilence(AudioFormat format, double gapMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(format);
        var ms = Math.Clamp(gapMilliseconds, 0, MaxFillMilliseconds);
        var frames = (int)Math.Round(format.SampleRate * ms / 1000.0);
        if (frames <= 0)
        {
            return Array.Empty<byte>();
        }

        return new byte[frames * format.BlockAlign];
    }

    public static byte[] CreateSilenceChunk(AudioFormat format) =>
        CreateSilence(format, ChunkMilliseconds);
}
