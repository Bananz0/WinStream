using WinStream.Core.Audio;

namespace WinStream.Tests;

public class BoundedAudioFrameQueueTests
{
    private static AudioFrame Frame(byte marker) =>
        new(new byte[] { marker }, new AudioFormat(44100, 2, 16), marker);

    [Fact]
    public void DropOldest_increments_queue_drop()
    {
        var queue = new BoundedAudioFrameQueue(capacity: 2);
        queue.Enqueue(Frame(1));
        queue.Enqueue(Frame(2));
        queue.Enqueue(Frame(3));

        Assert.Equal(1, queue.DropCount);
        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(2, first.Pcm.Span[0]);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(3, second.Pcm.Span[0]);
    }
}

public class AudioFrameSendPumpOffloadTests
{
    [Fact]
    public async Task Offload_does_not_send_on_producer_when_worker_blocked()
    {
        var sent = 0;
        await using var pump = new AudioFrameSendPump(
            capacity: 8,
            _ => Interlocked.Increment(ref sent));
        pump.BlockWorkerForTests();
        pump.Start();

        pump.Enqueue(new AudioFrame(
            new byte[4],
            new AudioFormat(44100, 2, 16),
            1));

        await Task.Delay(40);
        Assert.Equal(0, sent);
        Assert.Equal(0, pump.SendCount);

        pump.UnblockWorkerForTests();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref sent) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, sent);
        Assert.Equal(1, pump.SendCount);
    }
}
