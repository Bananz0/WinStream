#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Streaming.Link;

namespace WinStream.Audio;

/// <summary>
/// Link-only legacy WASAPI loopback capture. Tries a short client buffer and falls back
/// to 10 ms. This does not prove the audio-engine period and is never SLA-eligible.
/// Never used by AirPlay.
/// </summary>
public sealed class LinkWasapiLoopbackSource : ILinkCaptureSource
{
    public const int TargetBufferMilliseconds = LinkSlaEligibility.MaxCaptureContributionMs;
    public const int FallbackBufferMilliseconds = LinkSlaEligibility.FallbackCaptureBufferMs;

    private readonly object _gate = new();
    private readonly CaptureCallbackMeasurer _callbackMeasurer = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private AudioFormat? _format;
    private CaptureSampleFormat _sourceFormat;
    private string? _requestedEndpointId;
    private string? _activeEndpointId;
    private bool _disposed;
    private double _currentRms;
    private int _effectiveBufferMilliseconds = TargetBufferMilliseconds;
    private long _lastCallbackQpc;

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

    /// <summary>Requested client buffer after open (3 on success, 10 on fallback).</summary>
    public int EffectiveBufferMilliseconds => Volatile.Read(ref _effectiveBufferMilliseconds);

    public bool IsOwnedWinStreamEndpoint { get; private set; }

    public int MeasuredCaptureContributionMilliseconds =>
        _callbackMeasurer.MeasuredContributionMilliseconds;

    public bool IsSlaCaptureCapable =>
        IsOwnedWinStreamEndpoint &&
        LinkSlaEligibility.IsMeasuredCaptureSlaCapable(
            MeasuredCaptureContributionMilliseconds);

    public string? PreferredEndpointId
    {
        get => _requestedEndpointId;
        set => _requestedEndpointId = value;
    }

    /// <summary>
    /// Set only from <see cref="LinkCaptureEndpointPolicy"/> after the PnP instance id
    /// matches the WinStream VAD hardware id.
    /// </summary>
    public bool PreferredEndpointIsOwnedWinStreamVad { private get; set; }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _disposed = true;
        }

        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void StartCaptureLocked()
    {
        _enumerator ??= new MMDeviceEnumerator();
        _device = ResolveDevice(_enumerator, PreferredEndpointId);
        _activeEndpointId = _device.ID;
        IsOwnedWinStreamEndpoint =
            PreferredEndpointIsOwnedWinStreamVad &&
            string.Equals(_device.ID, PreferredEndpointId, StringComparison.OrdinalIgnoreCase);
        _callbackMeasurer.Reset();
        Interlocked.Exchange(ref _lastCallbackQpc, 0);

        try
        {
            var opened = LinkCaptureOpener.Open(
                StartCaptureWithBuffer,
                onAttemptFailed: (bufferMs, ex) =>
                    AppLog.Info("link", $"Link capture bufferMs={bufferMs} failed: {ex.Message}"));
            _capture = opened.Capture;
            Volatile.Write(ref _effectiveBufferMilliseconds, opened.AcceptedBufferMilliseconds);
            IsCapturing = true;
            AppLog.Info(
                "link",
                $"Link capture started endpoint={_device.FriendlyName} " +
                $"requestedBufferMs={opened.AcceptedBufferMilliseconds} " +
                $"fallback={opened.IsFallback} ownedVad={IsOwnedWinStreamEndpoint}");
        }
        catch
        {
            _device?.Dispose();
            _device = null;
            _activeEndpointId = null;
            IsOwnedWinStreamEndpoint = false;
            _callbackMeasurer.Reset();
            Interlocked.Exchange(ref _lastCallbackQpc, 0);
            throw;
        }
    }

    private WasapiCapture StartCaptureWithBuffer(int bufferMilliseconds)
    {
        var capture = new ShortBufferLoopbackCapture(
            _device!,
            bufferMilliseconds,
            useEventSync: IsOwnedWinStreamEndpoint);
        try
        {
            var waveFormat = capture.WaveFormat;
            _sourceFormat = ResolveSampleFormat(waveFormat);
            _format = new AudioFormat(waveFormat.SampleRate, waveFormat.Channels, 16);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            return capture;
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            throw;
        }
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
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _format is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastCallbackQpc, now);
        if (previous > 0)
        {
            _callbackMeasurer.RecordInterval(now - previous);
        }

        var copy = Pcm16Converter.ToPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), _sourceFormat);
        var rms = RmsCalculator.CalculatePcm16(copy);
        lock (_gate)
        {
            _currentRms = rms;
        }

        FrameAvailable?.Invoke(
            this,
            new AudioFrame(copy, _format, now));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            CaptureFailed?.Invoke(this, e.Exception);
        }

        DeviceInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static CaptureSampleFormat ResolveSampleFormat(WaveFormat waveFormat)
    {
        var encoding = waveFormat.Encoding;
        return encoding switch
        {
            WaveFormatEncoding.IeeeFloat => waveFormat.BitsPerSample switch
            {
                32 => CaptureSampleFormat.Float32,
                64 => CaptureSampleFormat.Float64,
                _ => throw new NotSupportedException(
                    $"Unsupported float depth {waveFormat.BitsPerSample}-bit.")
            },
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
                // Fall back to default.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private sealed class ShortBufferLoopbackCapture : WasapiCapture
    {
        public ShortBufferLoopbackCapture(
            MMDevice device,
            int bufferMilliseconds,
            bool useEventSync)
            : base(device, useEventSync, audioBufferMillisecondsLength: bufferMilliseconds)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
