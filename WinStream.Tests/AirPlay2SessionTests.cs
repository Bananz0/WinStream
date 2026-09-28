using System.Net;
using System.Net.Sockets;
using WinStream.Core.Network;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Protocol.Raop;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class AirPlay2SessionTests
{
    private static DeviceInfo TestReceiver() => new()
    {
        DisplayName = "Test",
        IPAddress = "127.0.0.1",
        Port = 7000,
        DeviceID = "AA:BB:CC:DD:EE:FF"
    };

    [Fact]
    public void SubmitPcm_is_noop_when_disconnected()
    {
        var receiver = new DeviceInfo
        {
            DisplayName = "Test",
            IPAddress = "127.0.0.1",
            Port = 7000,
            DeviceID = "AA:BB:CC:DD:EE:FF"
        };

        var session = new AirPlay2Session(receiver);
        Assert.Equal(SessionState.Disconnected, session.State);

        // Must not throw when no stream is active.
        session.SubmitPcm(new byte[352 * 4], new WinStream.Core.Audio.AudioFormat(44100, 2, 16));
    }

    [Fact]
    public void SharedMediaClockAlignment_adopts_shared_stamp_only_once()
    {
        uint rtp = 100;
        var pending = true;

        Assert.True(SharedMediaClockAlignment.Freeze(ref rtp, ref pending, 500));
        Assert.Equal(500u, rtp);
        Assert.False(pending);

        Assert.False(SharedMediaClockAlignment.Freeze(ref rtp, ref pending, 900));
        Assert.Equal(500u, rtp);
    }

    [Fact]
    public void SharedMediaClockAlignment_freezes_existing_base_without_shared_stamp()
    {
        // The unshared path keeps its own base, but must still settle so the
        // timeline anchor knows the timebase will no longer move.
        uint rtp = 100;
        var pending = true;

        Assert.True(SharedMediaClockAlignment.Freeze(ref rtp, ref pending, null));
        Assert.Equal(100u, rtp);
        Assert.False(pending);

        // A late shared stamp must not rebase a stream already anchored.
        Assert.False(SharedMediaClockAlignment.Freeze(ref rtp, ref pending, 900));
        Assert.Equal(100u, rtp);
    }

    [Fact]
    public async Task Disconnect_when_already_disconnected_is_noop()
    {
        var receiver = new DeviceInfo
        {
            DisplayName = "Test",
            IPAddress = "127.0.0.1",
            Port = 7000,
            DeviceID = "AA:BB:CC:DD:EE:FF"
        };

        await using var session = new AirPlay2Session(receiver);
        await session.DisconnectAsync();
        Assert.Equal(SessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task Streaming_fault_cancels_pending_freeze_barrier()
    {
        var receiver = new DeviceInfo
        {
            DisplayName = "Test",
            IPAddress = "127.0.0.1",
            Port = 7000,
            DeviceID = "AA:BB:CC:DD:EE:FF"
        };

        await using var session = new AirPlay2Session(receiver);
        var freeze = session.SeedStreamingFreezeBarrierForTests();
        Assert.Equal(SessionState.Streaming, session.State);
        Assert.False(freeze.IsCompleted);

        session.FailStreamingForTests("AirPlay event channel closed.");

        Assert.Equal(SessionState.Failed, session.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => freeze);
    }

    [Fact]
    public void SetEffectiveLatencyFrames_before_streaming_writes_field_directly()
    {
        // No burst/drop path exists before a receiver is actually bound — there is
        // nothing on the wire whose anchor could jump — so the setter is a bare
        // write, same as before this change.
        var session = new AirPlay2Session(TestReceiver());
        Assert.Equal(SessionState.Disconnected, session.State);

        session.SetEffectiveLatencyFrames(50_000);

        Assert.Equal(50_000u, session.EffectiveLatencyFrames);
    }

    [Fact]
    public async Task Raise_bursts_one_filler_packet_and_advances_head_with_latency()
    {
        await using var session = new AirPlay2Session(TestReceiver());
        _ = session.SeedStreamingFreezeBarrierForTests();

        using var fakeReceiver = new UdpClient(0);
        var receiverPort = ((IPEndPoint)fakeReceiver.Client.LocalEndPoint!).Port;
        using var audioSocket = new UdpClient(0);
        session.SeedNetworkForTests(
            audioSocket,
            new IPEndPoint(IPAddress.Loopback, receiverPort),
            new byte[32]);

        var before = session.EffectiveLatencyFrames;

        // A raise smaller than one packet interval still rounds up to exactly one
        // filler packet — the announced (head - latency) pairing only needs both
        // sides to move by the same amount, not any particular amount.
        session.SetEffectiveLatencyFrames(before + 1);

        Assert.Equal(before + AlacEncoder.FramesPerPacket, session.EffectiveLatencyFrames);

        var receiveTask = fakeReceiver.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(receiveTask, completed);

        var result = await receiveTask;
        var timestamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            result.Buffer.AsSpan(4));

        // The filler packet was stamped at the RTP head as it stood before the
        // raise (0, for a freshly seeded session) — proving the head advanced by
        // exactly one packet's worth of frames, the same amount latency did.
        Assert.Equal(0u, timestamp);
    }

    [Fact]
    public async Task Lower_only_takes_effect_on_next_silent_packet_without_advancing_rtp()
    {
        await using var session = new AirPlay2Session(TestReceiver());
        _ = session.SeedStreamingFreezeBarrierForTests();

        using var fakeReceiver = new UdpClient(0);
        var receiverPort = ((IPEndPoint)fakeReceiver.Client.LocalEndPoint!).Port;
        using var audioSocket = new UdpClient(0);
        session.SeedNetworkForTests(
            audioSocket,
            new IPEndPoint(IPAddress.Loopback, receiverPort),
            new byte[32]);

        var before = session.EffectiveLatencyFrames;
        session.SetEffectiveLatencyFrames(before - 1);

        // Recorded, not yet applied — a lower cannot burst anything without a
        // jump, so it waits for a genuinely silent outgoing packet.
        Assert.Equal(1, session.PendingLowerStepsForTests);
        Assert.Equal(before, session.EffectiveLatencyFrames);

        var silentPcm = new byte[AlacEncoder.PcmBytesPerPacket];
        session.SubmitPcm(silentPcm, new WinStream.Core.Audio.AudioFormat(44100, 2, 16));

        Assert.Equal(before - AlacEncoder.FramesPerPacket, session.EffectiveLatencyFrames);
        Assert.Equal(0, session.PendingLowerStepsForTests);

        // The dropped packet must never have reached the wire.
        var receiveTask = fakeReceiver.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(receiveTask, completed);
    }

    [Fact]
    public void PtpReseed_is_counted_as_an_observable_reanchor_event()
    {
        var session = new AirPlay2Session(TestReceiver());
        Assert.Equal(0, session.PtpReseedCount);

        session.RaisePtpReseededForTests(new PtpReseedEventArgs(100, 200, 0x1UL));
        session.RaisePtpReseededForTests(new PtpReseedEventArgs(200, 300, 0x1UL));

        Assert.Equal(2, session.PtpReseedCount);
    }
}
