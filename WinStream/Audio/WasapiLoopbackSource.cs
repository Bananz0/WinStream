#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinStream.Core.Audio;

namespace WinStream.Audio;

public sealed class WasapiLoopbackSource : IAudioSource
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<double> _recentRms = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private AudioFormat? _format;
    private CaptureSampleFormat _sourceFormat;
    private string? _requestedEndpointId;
    private string? _activeEndpointId;
    private bool _disposed;
    private double _currentRms;

    public event EventHandler<AudioFrame>? FrameAvailable;

    public event EventHandler<Exception>? CaptureFailed;

    public event EventHandler? DeviceInvalidated;

    public bool IsCapturing { get; private set; }

    public AudioFormat? Format
    {
        get
        {
            lock (_gate)
            {
                return _format;
            }
        }
    }

    public string? EndpointId
    {
        get
        {
            lock (_gate)
            {
                return _activeEndpointId;
            }
        }
    }

    public double CurrentRms
    {
        get
        {
            lock (_gate)
            {
                return _currentRms;
            }
        }
    }

    public bool IsSilent => RmsCalculator.IsSilent(CurrentRms);

    public string? PreferredEndpointId
    {
        get => _requestedEndpointId;
        set => _requestedEndpointId = value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (IsCapturing)
            {
                return Task.CompletedTask;
            }

            StartCaptureLocked();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            StopCaptureLocked();
        }

        return Task.CompletedTask;
    }

    public async Task RebindAsync(string? endpointId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (endpointId is not null)
        {
            PreferredEndpointId = endpointId;
        }

        var wasCapturing = false;
        lock (_gate)
        {
            wasCapturing = IsCapturing;
            StopCaptureLocked();
            if (wasCapturing)
            {
                StartCaptureLocked();
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            StopCaptureLocked();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private void StartCaptureLocked()
    {
        _enumerator ??= new MMDeviceEnumerator();
        _device = ResolveDevice(_enumerator, PreferredEndpointId);
        _activeEndpointId = _device.ID;
        _capture = new WasapiLoopbackCapture(_device);
        var waveFormat = _capture.WaveFormat;
        _sourceFormat = ResolveSampleFormat(waveFormat);

        // The pipeline downstream is 16-bit only, so publish the normalized format.
        _format = new AudioFormat(waveFormat.SampleRate, waveFormat.Channels, 16);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        IsCapturing = true;
    }

    private void StopCaptureLocked()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                if (_capture.CaptureState != CaptureState.Stopped)
                {
                    _capture.StopRecording();
                }
            }
            catch
            {
                // Device may already be gone.
            }

            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        IsCapturing = false;
        _currentRms = 0;
        _format = null;
        _activeEndpointId = null;
        while (_recentRms.TryDequeue(out _))
        {
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _format is null)
        {
            return;
        }

        var copy = Pcm16Converter.ToPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), _sourceFormat);
        var rms = RmsCalculator.CalculatePcm16(copy);
        lock (_gate)
        {
            _currentRms = rms;
            _recentRms.Enqueue(rms);
            while (_recentRms.Count > 50)
            {
                _recentRms.TryDequeue(out _);
            }
        }

        FrameAvailable?.Invoke(this, new AudioFrame(copy, _format, DateTime.UtcNow.Ticks));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            CaptureFailed?.Invoke(this, e.Exception);
            DeviceInvalidated?.Invoke(this, EventArgs.Empty);
            lock (_gate)
            {
                IsCapturing = false;
            }
        }
    }

    private static CaptureSampleFormat ResolveSampleFormat(WaveFormat waveFormat)
    {
        var encoding = waveFormat.Encoding;
        if (waveFormat is WaveFormatExtensible extensible)
        {
            try
            {
                encoding = extensible.ToStandardWaveFormat().Encoding;
            }
            catch (InvalidOperationException)
            {
                // Unknown subformat: 32-bit shared-mode mixes are float in practice.
                encoding = waveFormat.BitsPerSample == 32
                    ? WaveFormatEncoding.IeeeFloat
                    : WaveFormatEncoding.Pcm;
            }
        }

        return encoding switch
        {
            WaveFormatEncoding.IeeeFloat => waveFormat.BitsPerSample == 64
                ? CaptureSampleFormat.Float64
                : CaptureSampleFormat.Float32,
            WaveFormatEncoding.Pcm => waveFormat.BitsPerSample switch
            {
                16 => CaptureSampleFormat.Pcm16,
                24 => CaptureSampleFormat.Pcm24,
                32 => CaptureSampleFormat.Pcm32,
                _ => throw new NotSupportedException(
                    $"Unsupported capture depth {waveFormat.BitsPerSample}-bit.")
            },
            _ => throw new NotSupportedException($"Unsupported capture encoding {encoding}.")
        };
    }

    private static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? endpointId)
    {
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            try
            {
                return enumerator.GetDevice(endpointId);
            }
            catch
            {
                // Fall back to default when the saved endpoint disappeared.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
}
