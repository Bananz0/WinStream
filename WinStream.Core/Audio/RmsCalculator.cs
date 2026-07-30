namespace WinStream.Core.Audio;

public static class RmsCalculator
{
    public const double DefaultSilenceThreshold = 0.001;

    public static double CalculatePcm16(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2)
        {
            return 0;
        }

        var sampleCount = pcm.Length / 2;
        double sumSquares = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    public static bool IsSilent(double rms, double threshold = DefaultSilenceThreshold) =>
        rms < threshold;
}
