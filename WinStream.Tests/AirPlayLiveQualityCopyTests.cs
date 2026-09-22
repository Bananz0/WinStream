using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class AirPlayLiveQualityCopyTests
{
    [Fact]
    public void Auto_live_card_shows_the_exact_buffer()
    {
        var message = AirPlayLiveQualityCopy.For(
            PlaybackResponsiveness.Auto,
            LatencyAutoController.AutoStartFrames,
            AudioFidelity.Auto);

        Assert.Equal("47.891156 ms", message.Buffer);
        Assert.Contains("2,112 frames", message.Metrics);
        Assert.Contains("6 packets", message.Metrics);
        Assert.Contains("44,100 Hz", message.Metrics);
        Assert.Contains("1,411,200 bit/s PCM", message.Metrics);
        Assert.Contains("2,112 frames", message.Tooltip);
    }

    [Fact]
    public void A_climbed_auto_buffer_reports_exact_milliseconds()
    {
        var message = AirPlayLiveQualityCopy.For(
            PlaybackResponsiveness.Auto,
            LatencyAutoController.CeilingFrames,
            AudioFidelity.HighFidelity);

        Assert.Equal("2000 ms", message.Buffer);
        Assert.Contains("88,200 frames", message.Metrics);
        Assert.Contains("High fidelity", message.Metrics);
    }

    [Fact]
    public void Extreme_reports_the_exact_packet_floor_not_a_round_number()
    {
        var message = AirPlayLiveQualityCopy.For(
            PlaybackResponsiveness.LabPacket,
            LatencyAutoController.LabPacketFrames,
            AudioFidelity.Standard);

        Assert.Equal("47.891156 ms", message.Buffer);
        Assert.Contains("2,112 frames", message.Metrics);
        Assert.Contains("6 packets", message.Metrics);
        Assert.Contains("Extreme", message.Metrics);
        Assert.Contains("Standard fidelity", message.Metrics);
    }

    [Fact]
    public void Milliseconds_are_exact_frame_clock_conversions()
    {
        Assert.Equal(250.0, AirPlayLiveQualityCopy.Milliseconds(11025));
        Assert.Equal(2000.0, AirPlayLiveQualityCopy.Milliseconds(88200));
        Assert.Equal(
            2112 * 1000.0 / 44100,
            AirPlayLiveQualityCopy.Milliseconds(2112));
    }

    [Fact]
    public void Tooltip_is_honest_about_delay_and_output_format()
    {
        var message = AirPlayLiveQualityCopy.For(
            PlaybackResponsiveness.Balanced,
            LatencyAutoController.BalancedFrames,
            AudioFidelity.Auto);

        Assert.Contains("lossless ALAC", message.Tooltip);
        Assert.Contains("44,100 Hz stereo 16-bit", message.Tooltip);
        Assert.Contains("mid-session without re-SETUP", message.Tooltip);
        Assert.Contains("Not a measured end-to-end delay", message.Tooltip);
    }

    [Fact]
    public void Idle_panel_explains_that_nothing_is_streaming()
    {
        Assert.Equal("Not streaming", AirPlayLiveQualityCopy.IdleBuffer);
        Assert.Contains("Connect a receiver", AirPlayLiveQualityCopy.IdleMetrics);
    }

    [Fact]
    public void Buffer_increase_reports_exact_direction_and_values()
    {
        Assert.Equal(
            "Buffer increased: 79.818594 ms → 250 ms (3,520 → 11,025 frames)",
            AirPlayLiveQualityCopy.BufferChange(3520, 11025));
    }

    [Fact]
    public void Buffer_decrease_reports_exact_direction_and_values()
    {
        Assert.Equal(
            "Buffer decreased: 2000 ms → 250 ms (88,200 → 11,025 frames)",
            AirPlayLiveQualityCopy.BufferChange(88200, 11025));
    }

    [Fact]
    public void Live_activity_reports_cumulative_packets_and_measured_rate()
    {
        var line = AirPlayLiveQualityCopy.LiveActivity(
            packetsSent: 125_432,
            packetsPerSecond: 125.28,
            queueDepth: 2,
            drops: 0,
            slowSends: 3,
            reanchors: 4);

        Assert.Equal(
            "125,432 packets sent · 125.3 pkt/s · queue 2 · 0 drops · 3 slow sends · 4 re-anchors",
            line);
    }

    [Fact]
    public void Status_compact_shows_buffer_and_measured_rate()
    {
        Assert.Equal(
            "47.891156 ms · 125.3 pkt/s",
            AirPlayLiveQualityCopy.StatusCompact(2112, 125.28));
    }
}
