using WinStream.Core.Network;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class AirPlay2SessionTests
{
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
    public async Task TimelineAnchorGate_does_not_publish_before_freeze()
    {
        var frozen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var run = TimelineAnchorGate.RunAfterFreezeAsync(
            frozen.Task,
            _ =>
            {
                published.TrySetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await Task.Delay(25);
        Assert.False(published.Task.IsCompleted);

        frozen.SetResult();
        await run;
        Assert.True(published.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TimelineAnchorGate_cancellation_prevents_publication()
    {
        var frozen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var published = false;
        using var cancellation = new CancellationTokenSource();

        var run = TimelineAnchorGate.RunAfterFreezeAsync(
            frozen.Task,
            _ =>
            {
                published = true;
                return Task.CompletedTask;
            },
            cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(published);
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
}
