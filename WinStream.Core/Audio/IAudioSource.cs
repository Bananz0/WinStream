namespace WinStream.Core.Audio;

public interface IAudioSource : IAsyncDisposable
{
    event EventHandler<AudioFrame>? FrameAvailable;

    event EventHandler<Exception>? CaptureFailed;

    event EventHandler? DeviceInvalidated;

    /// <summary>
    /// A filled capture gap, delivered as pre-chunked silence frames the caller should
    /// hand to a burst-capable sender (e.g. <c>AudioFrameSendPump.EnqueueBurst</c>)
    /// instead of the normal paced path — the gap already cost real time, so catching
    /// it up must not cost more by re-pacing it. Default no-op: only a source that
    /// actually detects gaps (loopback capture) need implement this.
    /// </summary>
    event EventHandler<IReadOnlyList<AudioFrame>>? GapBurstAvailable
    {
        add { }
        remove { }
    }

    bool IsCapturing { get; }

    AudioFormat? Format { get; }

    string? EndpointId { get; }

    double CurrentRms { get; }

    bool IsSilent { get; }

    /// <summary>
    /// Cumulative count of capture gaps filled with silence so far. Default 0 for a
    /// source that cannot experience a capture gap (e.g. a synthetic test source).
    /// </summary>
    long GapCount => 0;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
