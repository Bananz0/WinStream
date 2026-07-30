namespace WinStream.Core.Audio;

public interface IAudioSource : IAsyncDisposable
{
    event EventHandler<AudioFrame>? FrameAvailable;

    event EventHandler<Exception>? CaptureFailed;

    event EventHandler? DeviceInvalidated;

    bool IsCapturing { get; }

    AudioFormat? Format { get; }

    string? EndpointId { get; }

    double CurrentRms { get; }

    bool IsSilent { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
