namespace WinStream.Core.Audio;

public sealed class FakeAudioSource : IAudioSource
{
    private readonly AudioFormat _format;
    private readonly byte[] _pattern;
    private readonly TimeSpan _frameInterval;
    private readonly double _forcedRms;
    private CancellationTokenSource? _runCts;
    private Task? _pumpTask;
    private long _timestampTicks;
    private bool _disposed;

    public FakeAudioSource(
        AudioFormat? format = null,
        byte[]? pattern = null,
        TimeSpan? frameInterval = null,
        double forcedRms = 0.25)
    {
        _format = format ?? new AudioFormat(44100, 2, 16);
        _pattern = pattern ?? CreateSineFrame(_format, 440, 0.25);
        _frameInterval = frameInterval ?? TimeSpan.FromMilliseconds(20);
        _forcedRms = forcedRms;
        CurrentRms = forcedRms;
    }

    public event EventHandler<AudioFrame>? FrameAvailable;

    public event EventHandler<Exception>? CaptureFailed;

    public event EventHandler? DeviceInvalidated;

    public bool IsCapturing { get; private set; }

    public AudioFormat? Format => IsCapturing ? _format : null;

    public string? EndpointId { get; set; } = "fake-endpoint";

    public double CurrentRms { get; private set; }

    public bool IsSilent => RmsCalculator.IsSilent(CurrentRms);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing)
        {
            return Task.CompletedTask;
        }

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsCapturing = true;
        CurrentRms = _forcedRms;
        _pumpTask = Task.Run(() => PumpAsync(_runCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing)
        {
            return;
        }

        _runCts?.Cancel();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        IsCapturing = false;
        CurrentRms = 0;
        _runCts?.Dispose();
        _runCts = null;
        _pumpTask = null;
    }

    public void SimulateDeviceInvalidation() => DeviceInvalidated?.Invoke(this, EventArgs.Empty);

    public void SimulateFailure(Exception exception) => CaptureFailed?.Invoke(this, exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CurrentRms = RmsCalculator.CalculatePcm16(_pattern);
                if (CurrentRms <= 0)
                {
                    CurrentRms = _forcedRms;
                }

                _timestampTicks += _frameInterval.Ticks;
                FrameAvailable?.Invoke(this, new AudioFrame(_pattern, _format, _timestampTicks));
                await Task.Delay(_frameInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(this, ex);
            IsCapturing = false;
        }
    }

    private static byte[] CreateSineFrame(AudioFormat format, double frequency, double amplitude)
    {
        var frames = Math.Max(1, format.SampleRate / 50);
        var buffer = new byte[frames * format.BlockAlign];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * amplitude * short.MaxValue);
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = (i * format.Channels + channel) * 2;
                buffer[offset] = (byte)(sample & 0xff);
                buffer[offset + 1] = (byte)((sample >> 8) & 0xff);
            }
        }

        return buffer;
    }
}
