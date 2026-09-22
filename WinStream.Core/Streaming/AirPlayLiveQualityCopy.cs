using System.Globalization;
using WinStream.Core.Audio;

namespace WinStream.Core.Streaming;

/// <summary>Formats the single live AirPlay telemetry panel with exact values.</summary>
public static class AirPlayLiveQualityCopy
{
    public const int OutputSampleRate = 44100;
    public const int OutputChannels = 2;
    public const int OutputBitsPerSample = 16;
    public const int FramesPerPacket = AudioPacingConstants.PacketFrames;

    /// <summary>Uncompressed PCM bitrate of the ALAC source stream (bit/s).</summary>
    public const int PcmBitRate =
        OutputSampleRate * OutputChannels * OutputBitsPerSample;

    public readonly record struct Message(
        string Buffer,
        string Metrics,
        string Tooltip);

    public static Message For(
        PlaybackResponsiveness responsiveness,
        uint effectiveLatencyFrames,
        AudioFidelity fidelity)
    {
        var responsivenessLabel = ResponsivenessLabel(responsiveness);
        var fidelityLabel = FidelityLabel(fidelity);
        var buffer = BufferLabel(effectiveLatencyFrames);
        var packets = PacketCountLabel(effectiveLatencyFrames);
        var packetRate = PacketRateLabel();

        return new Message(
            buffer,
            $"{effectiveLatencyFrames:N0} frames · {packets} · {responsivenessLabel} · " +
            $"{fidelityLabel} · ALAC · {OutputSampleRate:N0} Hz / {OutputBitsPerSample}-bit / " +
            $"{OutputChannels} ch · {packetRate} · {PcmBitRate:N0} bit/s PCM",
            $"Exact speaker buffer {buffer} " +
            $"({effectiveLatencyFrames:N0} frames at {OutputSampleRate:N0} Hz). " +
            $"{responsivenessLabel} responsiveness; {fidelityLabel}. " +
            $"Output is lossless ALAC from {OutputSampleRate:N0} Hz stereo {OutputBitsPerSample}-bit PCM " +
            $"({FramesPerPacket} frames/packet, {packetRate}, {PcmBitRate:N0} bit/s). " +
            "This is the requested speaker buffer at connect time; Auto may adjust the sync offset " +
            "mid-session without re-SETUP. Not a measured end-to-end delay.");
    }

    public static string IdleBuffer => "Not streaming";

    public static string IdleMetrics =>
        "Connect a receiver to see exact live AirPlay metrics.";

    /// <summary>
    /// Compact status-pill line: speaker buffer plus the measured send rate.
    /// </summary>
    public static string StatusCompact(uint effectiveLatencyFrames, double packetsPerSecond)
    {
        var rate = packetsPerSecond.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{BufferLabel(effectiveLatencyFrames)} · {rate} pkt/s";
    }

    /// <summary>
    /// The continuously-moving line: cumulative packets, the measured send rate over
    /// the last poll, and current queue/drop/slow counts.
    /// </summary>
    public static string LiveActivity(
        long packetsSent,
        double packetsPerSecond,
        int queueDepth,
        long drops,
        long slowSends,
        long reanchors = 0)
    {
        var rate = packetsPerSecond.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{packetsSent:N0} packets sent · {rate} pkt/s · queue {queueDepth:N0} · " +
               $"{drops:N0} drops · {slowSends:N0} slow sends · {reanchors:N0} re-anchors";
    }

    public static string BufferChange(uint previousFrames, uint currentFrames)
    {
        if (previousFrames == currentFrames)
        {
            return "Live metrics update automatically while streaming.";
        }

        var direction = currentFrames > previousFrames ? "increased" : "decreased";
        return $"Buffer {direction}: {BufferLabel(previousFrames)} → {BufferLabel(currentFrames)} " +
               $"({previousFrames:N0} → {currentFrames:N0} frames)";
    }

    public static double Milliseconds(uint effectiveLatencyFrames) =>
        effectiveLatencyFrames * 1000.0 / OutputSampleRate;

    public static string BufferLabel(uint effectiveLatencyFrames)
    {
        // One frame is 1000/44100 ms ≈ 0.022676 ms; six decimals keep each frame distinct.
        return Milliseconds(effectiveLatencyFrames)
            .ToString("0.######", CultureInfo.InvariantCulture) + " ms";
    }

    private static string PacketCountLabel(uint effectiveLatencyFrames)
    {
        var packets = effectiveLatencyFrames / (double)FramesPerPacket;
        return packets.ToString("0.######", CultureInfo.InvariantCulture) + " packets";
    }

    private static string PacketRateLabel()
    {
        var packetsPerSecond = OutputSampleRate / (double)FramesPerPacket;
        return packetsPerSecond.ToString("0.######", CultureInfo.InvariantCulture) + " pkt/s";
    }

    private static string ResponsivenessLabel(PlaybackResponsiveness responsiveness) =>
        responsiveness switch
        {
            PlaybackResponsiveness.Auto => "Auto",
            PlaybackResponsiveness.LabPacket => "Extreme",
            PlaybackResponsiveness.Experimental => "Experimental",
            PlaybackResponsiveness.VeryLow => "Very low",
            PlaybackResponsiveness.LowDelay => "Low delay",
            PlaybackResponsiveness.Balanced => "Balanced",
            PlaybackResponsiveness.MostStable => "Most stable",
            _ => responsiveness.ToString()
        };

    private static string FidelityLabel(AudioFidelity fidelity) =>
        fidelity switch
        {
            AudioFidelity.HighFidelity => "High fidelity",
            AudioFidelity.Standard => "Standard fidelity",
            _ => "Auto fidelity"
        };
}
