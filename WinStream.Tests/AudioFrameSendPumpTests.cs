using System.Runtime;
using WinStream.Core.Audio;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

/// <summary>
/// <see cref="AudioFrameSendPump"/>'s GC latency mode hint is process-global
/// (reference-counted across concurrent pumps by design — see
/// <c>AudioFrameSendPump.TryEnterSustainedLowLatency</c>). The test classes below
/// all construct pumps, so they must not run as separate parallel xUnit
/// collections or one test's pump lifetime can straddle another's and make the
/// GC-mode assertions flaky; this shared, non-parallelized collection keeps them
/// serialized relative to each other while still running in parallel with the
/// rest of the suite.
/// </summary>
[CollectionDefinition("AudioFrameSendPump", DisableParallelization = true)]
public class AudioFrameSendPumpCollection;

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

[Collection("AudioFrameSendPump")]
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
        using var entered = pump.ArmWorkerEnteredSignalForTests();
        pump.Start();

        pump.Enqueue(new AudioFrame(
            new byte[4],
            new AudioFormat(44100, 2, 16),
            1));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
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

    [Fact]
    public async Task Enqueue_overflow_increments_QueueDropCount()
    {
        await using var pump = new AudioFrameSendPump(
            capacity: 2,
            _ => { });

        // Do not Start — assert queue policy without a racing worker drain.
        pump.Enqueue(new AudioFrame(new byte[] { 1 }, new AudioFormat(44100, 2, 16), 1));
        pump.Enqueue(new AudioFrame(new byte[] { 2 }, new AudioFormat(44100, 2, 16), 2));
        pump.Enqueue(new AudioFrame(new byte[] { 3 }, new AudioFormat(44100, 2, 16), 3));

        Assert.Equal(1, pump.QueueDropCount);
        Assert.Equal(2, pump.QueueDepth);
    }
}

[Collection("AudioFrameSendPump")]
public class AudioFrameSendPumpPacingTests
{
    private const int PacketBytes = AlacEncoder.PcmBytesPerPacket;

    /// <summary>Collects chunks without waiting, so pacing math is asserted in real time.</summary>
    private static async Task<List<byte[]>> DrainAsync(
        AudioFrame frame,
        int expectedChunks,
        Func<IDisposable?>? elevateCurrentThread = null)
    {
        var chunks = new List<byte[]>();
        var gate = new object();
        await using var pump = new AudioFrameSendPump(
            capacity: 8,
            chunk =>
            {
                lock (gate)
                {
                    chunks.Add(chunk.Pcm.ToArray());
                }
            },
            elevateCurrentThread,
            waitUntilDue: (_, token) => !token.IsCancellationRequested);

        pump.Start();
        pump.Enqueue(frame);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (chunks.Count >= expectedChunks)
                {
                    break;
                }
            }

            await Task.Delay(5);
        }

        lock (gate)
        {
            return chunks.ToList();
        }
    }

    [Fact]
    public async Task Capture_frame_is_released_as_packet_sized_chunks()
    {
        // 50 ms of 44.1 kHz stereo — what one classic loopback callback delivers.
        const int sourceFrames = 44100 * 50 / 1000;
        var pcm = new byte[sourceFrames * 4];
        Random.Shared.NextBytes(pcm);
        var format = new AudioFormat(44100, 2, 16);

        var expectedChunks =
            (sourceFrames + AlacEncoder.FramesPerPacket - 1) / AlacEncoder.FramesPerPacket;
        var chunks = await DrainAsync(new AudioFrame(pcm, format, 1), expectedChunks);

        Assert.Equal(expectedChunks, chunks.Count);
        Assert.All(chunks.Take(chunks.Count - 1), chunk => Assert.Equal(PacketBytes, chunk.Length));
        Assert.Equal(pcm, chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public async Task Resampled_source_is_paced_by_output_duration_not_chunk_count()
    {
        // One second at 48 kHz must schedule ~44100 output frames, not one packet
        // period per chunk — otherwise a 48 kHz mix paces slower than real time.
        const int sourceFrames = 48_000;
        var format = new AudioFormat(48_000, 2, 16);
        var pcm = new byte[sourceFrames * 4];

        var chunkSourceFrames = AlacEncoder.FramesPerPacket * 48_000 / 44_100;
        var expectedChunks = (sourceFrames + chunkSourceFrames - 1) / chunkSourceFrames;
        var chunks = await DrainAsync(new AudioFrame(pcm, format, 1), expectedChunks);

        Assert.Equal(expectedChunks, chunks.Count);
        var scheduledFrames = chunks.Sum(
            chunk => (long)PcmPacketBuffer.EstimateOutputFrames(chunk.Length, format));
        Assert.InRange(scheduledFrames, 44_100 - AlacEncoder.FramesPerPacket, 44_100);
    }

    [Fact]
    public async Task Thread_elevation_failure_leaves_the_pump_streaming()
    {
        var pcm = new byte[PacketBytes];
        var chunks = await DrainAsync(
            new AudioFrame(pcm, new AudioFormat(44100, 2, 16), 1),
            expectedChunks: 1,
            elevateCurrentThread: () => throw new InvalidOperationException("no MMCSS here"));

        Assert.Single(chunks);
    }

    [Fact]
    public async Task Thread_elevation_is_released_when_the_worker_stops()
    {
        var reverted = false;
        var pump = new AudioFrameSendPump(
            capacity: 4,
            _ => { },
            () => new CallbackDisposable(() => reverted = true),
            waitUntilDue: (_, token) => !token.IsCancellationRequested);

        pump.Start();
        pump.Enqueue(new AudioFrame(
            new byte[PacketBytes],
            new AudioFormat(44100, 2, 16),
            1));

        await pump.DisposeAsync();

        Assert.True(reverted);
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

[Collection("AudioFrameSendPump")]
public class AudioFrameSendPumpBurstTests
{
    [Fact]
    public async Task EnqueueBurst_releases_frames_without_a_pacing_wait()
    {
        var sent = new List<byte>();
        var gate = new object();
        // waitUntilDue would fail the test if a burst frame ever went through the
        // paced path — burst frames must never call it.
        await using var pump = new AudioFrameSendPump(
            capacity: 8,
            chunk =>
            {
                lock (gate)
                {
                    sent.Add(chunk.Pcm.Span[0]);
                }
            },
            waitUntilDue: (_, _) => throw new InvalidOperationException(
                "burst frames must not be paced"));

        pump.Start();
        var format = new AudioFormat(44100, 2, 16);
        pump.EnqueueBurst(new[]
        {
            new AudioFrame(new byte[] { 1 }, format, 1),
            new AudioFrame(new byte[] { 2 }, format, 2),
        });

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (sent.Count >= 2)
                {
                    break;
                }
            }

            await Task.Delay(5);
        }

        lock (gate)
        {
            Assert.Equal(new byte[] { 1, 2 }, sent);
        }
    }

    [Fact]
    public void EnqueueBurst_rejects_null_frames()
    {
        var pump = new AudioFrameSendPump(capacity: 8, _ => { });

        Assert.Throws<ArgumentNullException>(() => pump.EnqueueBurst(null!));
    }

    [Fact]
    public async Task EnqueueBurst_after_dispose_throws()
    {
        var pump = new AudioFrameSendPump(capacity: 8, _ => { });
        await pump.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => pump.EnqueueBurst(Array.Empty<AudioFrame>()));
    }

    [Fact]
    public async Task StallCount_increments_when_lateness_exceeds_a_tightened_budget()
    {
        var format = new AudioFormat(44100, 2, 16);
        await using var pump = new AudioFrameSendPump(
            capacity: 8,
            _ => { },
            waitUntilDue: (_, token) => !token.IsCancellationRequested);

        // A zero-frame budget means any real lateness at all forces a re-anchor.
        pump.SetLatenessBudget(0);
        pump.Start();

        pump.Enqueue(new AudioFrame(new byte[AlacEncoder.PcmBytesPerPacket], format, 1));

        var firstSent = DateTime.UtcNow.AddSeconds(2);
        while (pump.SendCount == 0 && DateTime.UtcNow < firstSent)
        {
            await Task.Delay(5);
        }

        Assert.Equal(1, pump.SendCount);

        // Let real time run past the packet's ~8 ms period before the next frame
        // arrives, so the scheduler sees lateness beyond the zero-frame budget.
        await Task.Delay(50);
        pump.Enqueue(new AudioFrame(new byte[AlacEncoder.PcmBytesPerPacket], format, 2));

        var secondSent = DateTime.UtcNow.AddSeconds(2);
        while (pump.SendCount < 2 && DateTime.UtcNow < secondSent)
        {
            await Task.Delay(5);
        }

        Assert.Equal(2, pump.SendCount);
        Assert.True(pump.StallCount >= 1);
        Assert.Equal(pump.StallCount, pump.CatchUpClampCount);
    }

    [Fact]
    public async Task Start_raises_sustained_low_latency_and_dispose_restores_it()
    {
        var originalMode = GCSettings.LatencyMode;
        var pump = new AudioFrameSendPump(capacity: 4, _ => { });

        pump.Start();
        try
        {
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        }
        finally
        {
            await pump.DisposeAsync();
        }

        Assert.Equal(originalMode, GCSettings.LatencyMode);
    }

    [Fact]
    public async Task Two_overlapping_pumps_do_not_let_one_disposal_reset_the_other()
    {
        // Multi-room fan-out gives each session its own pump; one room
        // disconnecting must not silently downgrade GC latency mode for a
        // still-streaming second room. This is the scenario the process-wide
        // reference count (not a per-instance save/restore) exists to handle.
        var originalMode = GCSettings.LatencyMode;
        var first = new AudioFrameSendPump(capacity: 4, _ => { });
        var second = new AudioFrameSendPump(capacity: 4, _ => { });

        first.Start();
        second.Start();
        Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);

        await first.DisposeAsync();
        Assert.Equal(
            GCLatencyMode.SustainedLowLatency,
            GCSettings.LatencyMode);

        await second.DisposeAsync();
        Assert.Equal(originalMode, GCSettings.LatencyMode);
    }
}
