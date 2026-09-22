using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkPlayoutBufferTests
{
    private static readonly AudioFormat Format = new(
        Wsl1Constants.DefaultSampleRate,
        Wsl1Constants.DefaultChannels,
        16);

    private static readonly DateTimeOffset Start = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_released_until_the_jitter_target_is_primed()
    {
        var buffer = Create();
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];

        var push = buffer.Push(0, packet, Start);

        Assert.False(push.StartedPlayout);
        Assert.False(buffer.IsPlaying);
        Assert.False(buffer.TryDrain(out _));
        Assert.Equal(packet.Length, buffer.QueuedBytes);
    }

    [Fact]
    public void Playout_starts_once_the_queue_reaches_the_target_and_then_drains()
    {
        var buffer = Create();

        var pushes = FillToTarget(buffer, Start);

        Assert.True(pushes[^1].StartedPlayout);
        Assert.True(buffer.IsPlaying);

        var drained = 0;
        while (buffer.TryDrain(out var chunk))
        {
            drained += chunk.Length;
        }

        Assert.Equal(pushes.Count * Wsl1Constants.DefaultPayloadBytes, drained);
        Assert.Equal(0, buffer.QueuedBytes);
    }

    [Fact]
    public void Sequence_gaps_are_counted_as_late_or_lost()
    {
        var buffer = Create();
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];

        buffer.Push(0, packet, Start);
        var skipped = buffer.Push(5, packet, Start);
        var inOrder = buffer.Push(6, packet, Start);

        Assert.True(skipped.WasLate);
        Assert.False(inOrder.WasLate);
        Assert.Equal(1, buffer.LateOrLostPackets);
    }

    [Fact]
    public void Sequence_wraparound_is_not_treated_as_a_gap()
    {
        var buffer = Create();
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];

        buffer.Push(ushort.MaxValue, packet, Start);
        var wrapped = buffer.Push(0, packet, Start);

        Assert.False(wrapped.WasLate);
        Assert.Equal(0, buffer.LateOrLostPackets);
    }

    [Fact]
    public void Starved_sink_grows_the_target_and_forces_a_reprime()
    {
        var buffer = Create();
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];
        var seq = (ushort)0;
        var now = Start;
        while (!buffer.IsPlaying)
        {
            buffer.Push(seq++, packet, now);
        }

        var startTarget = buffer.TargetMilliseconds;
        while (buffer.TryDrain(out _))
        {
        }

        // Past the startup grace so the controller is allowed to react.
        now = Start.AddSeconds(2);
        var push = buffer.Push(seq, packet, now, sinkStarved: true);

        Assert.True(push.WasStarved);
        Assert.True(push.PausedForReprime);
        Assert.False(buffer.IsPlaying);
        Assert.Equal(1, buffer.Underruns);
        Assert.Equal(1, buffer.Repriming);
        Assert.True(buffer.TargetMilliseconds > startTarget);
    }

    [Fact]
    public void Receivers_that_cannot_see_their_sink_never_report_underruns()
    {
        var buffer = Create();
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];
        FillToTarget(buffer, Start);

        buffer.Push(9999, packet, Start.AddSeconds(2));

        Assert.Equal(0, buffer.Underruns);
        Assert.Equal(1, buffer.LateOrLostPackets);
    }

    [Fact]
    public void Ethernet_and_wifi_prime_to_their_own_start_targets()
    {
        Assert.Equal(
            LinkJitterController.EthernetStartMs,
            Create(ethernet: true).TargetMilliseconds);
        Assert.Equal(
            LinkJitterController.OtherStartMs,
            Create(ethernet: false).TargetMilliseconds);
    }

    [Fact]
    public void Backlog_beyond_capacity_drops_the_oldest_audio_instead_of_growing_latency()
    {
        var buffer = new LinkPlayoutBuffer(
            Format,
            pathIsEthernet: true,
            Start,
            capacityMilliseconds: LinkJitterController.MaxMs);
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];
        var capacityBytes = Format.AverageBytesPerSecond * LinkJitterController.MaxMs / 1000;

        for (var i = 0; i < 500; i++)
        {
            buffer.Push((ushort)i, packet, Start);
        }

        Assert.True(buffer.QueuedBytes <= capacityBytes);
        Assert.True(buffer.DroppedBytes > 0);
    }

    [Fact]
    public void Capacity_below_the_jitter_ceiling_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinkPlayoutBuffer(
            Format,
            pathIsEthernet: true,
            Start,
            capacityMilliseconds: LinkJitterController.MaxMs - 1));
    }

    private static LinkPlayoutBuffer Create(bool ethernet = true) =>
        new(Format, ethernet, Start);

    private static List<LinkPlayoutPush> FillToTarget(LinkPlayoutBuffer buffer, DateTimeOffset now)
    {
        var packet = new byte[Wsl1Constants.DefaultPayloadBytes];
        var pushes = new List<LinkPlayoutPush>();
        var seq = (ushort)0;
        while (!buffer.IsPlaying)
        {
            pushes.Add(buffer.Push(seq++, packet, now));
        }

        return pushes;
    }
}
