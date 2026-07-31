#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinStream.Core.Audio;
using WinStream.Core.Logging;

namespace WinStream.Audio;

public sealed class WasapiLoopbackSource : IAudioSource
{
    /// <summary>
    /// NAudio's <see cref="WasapiLoopbackCapture"/> hard-codes a 100 ms client buffer that it
    /// polls every 50 ms, so audio arrives in ~60 ms bursts. Halving it keeps the poll well
    /// inside <see cref="CaptureGapFiller.ThresholdMilliseconds"/> while leaving the client
    /// ring twice as long as the poll interval.
    /// </summary>
    private const int CaptureBufferMilliseconds = 50;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<double> _recentRms = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private AudioFormat? _format;
    private CaptureSampleFormat _sourceFormat;
    private string? _requestedEndpointId;
    private string? _activeEndpointId;
    private bool _disposed;
    private double _currentRms;
    private long _lastCallbackQpc;
    private long _captureGapCount;
    private long _lastInterCallbackTicks;
    private int _inGap;
    private CancellationTokenSource? _gapFillCts;
    private Task? _gapFillLoop;

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

    /// <summary>How many sustained capture gaps were opened (developer telemetry).</summary>
    internal long CaptureGapCount => Interlocked.Read(ref _captureGapCount);

    /// <summary>Most recent inter-callback interval in Stopwatch ticks.</summary>
    internal long LastInterCallbackTicks => Interlocked.Read(ref _lastInterCallbackTicks);

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
        CancellationTokenSource? gapCts;
        Task? gapLoop;
        lock (_gate)
        {
            gapCts = _gapFillCts;
            gapLoop = _gapFillLoop;
            _gapFillCts = null;
            _gapFillLoop = null;
            StopCaptureLocked();
        }

        JoinGapFill(gapCts, gapLoop);
        return Task.CompletedTask;
    }

    public async Task RebindAsync(string? endpointId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (endpointId is not null)
        {
            PreferredEndpointId = endpointId;
        }

        CancellationTokenSource? gapCts;
        Task? gapLoop;
        var wasCapturing = false;
        lock (_gate)
        {
            wasCapturing = IsCapturing;
            gapCts = _gapFillCts;
            gapLoop = _gapFillLoop;
            _gapFillCts = null;
            _gapFillLoop = null;
            StopCaptureLocked();
        }

        JoinGapFill(gapCts, gapLoop);

        if (wasCapturing)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    StartCaptureLocked();
                }
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

        CancellationTokenSource? gapCts;
        Task? gapLoop;
        lock (_gate)
        {
            gapCts = _gapFillCts;
            gapLoop = _gapFillLoop;
            _gapFillCts = null;
            _gapFillLoop = null;
            StopCaptureLocked();
            _disposed = true;
        }

        JoinGapFill(gapCts, gapLoop);
        _enumerator?.Dispose();
        _enumerator = null;
        return ValueTask.CompletedTask;
    }

    private void StartCaptureLocked()
    {
        _enumerator ??= new MMDeviceEnumerator();
        _device = ResolveDevice(_enumerator, PreferredEndpointId);
        _activeEndpointId = _device.ID;
        _capture = new ShortBufferLoopbackCapture(_device, CaptureBufferMilliseconds);
        var waveFormat = _capture.WaveFormat;
        _sourceFormat = ResolveSampleFormat(waveFormat);

        // The pipeline downstream is 16-bit only, so publish the normalized format.
        _format = new AudioFormat(waveFormat.SampleRate, waveFormat.Channels, 16);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        IsCapturing = true;
        Interlocked.Exchange(ref _lastCallbackQpc, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _inGap, 0);
        _gapFillCts = new CancellationTokenSource();
        var token = _gapFillCts.Token;
        _gapFillLoop = Task.Run(() => RunGapFillLoopAsync(token), token);
    }

    private void StopCaptureLocked()
    {
        _gapFillCts?.Cancel();

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
        Interlocked.Exchange(ref _lastCallbackQpc, 0);
        Interlocked.Exchange(ref _inGap, 0);
        while (_recentRms.TryDequeue(out _))
        {
        }
    }

    private static void JoinGapFill(CancellationTokenSource? gapCts, Task? gapLoop)
    {
        gapCts?.Cancel();
        try
        {
            gapLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Expected on cancel.
        }

        gapCts?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _format is null)
        {
            return;
        }

        // Gap silence is timer-only (RunGapFillLoopAsync) so resume and the timer
        // never double-insert. Callbacks only refresh QPC / telemetry.
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastCallbackQpc, now);
        CaptureGapFiller.EndGap(ref _inGap);
        if (previous != 0)
        {
            Interlocked.Exchange(ref _lastInterCallbackTicks, now - previous);
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

    private async Task RunGapFillLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(CaptureGapFiller.ChunkMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Volatile.Read(ref _disposed) && !IsCapturing)
            {
                return;
            }

            var format = _format;
            if (format is null || !IsCapturing)
            {
                continue;
            }

            var last = Interlocked.Read(ref _lastCallbackQpc);
            if (last == 0)
            {
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var delta = now - last;

            // Only a silence longer than the poll cadence counts as a gap. Once one is
            // open, keep filling every tick until a real callback closes it.
            if (Volatile.Read(ref _inGap) == 0)
            {
                if (!CaptureGapFiller.IsGap(delta, Stopwatch.Frequency))
                {
                    continue;
                }

                if (CaptureGapFiller.TryBeginGap(ref _inGap, ref _captureGapCount))
                {
                    AppLog.Info(
                        "capture",
                        "Loopback gap — inserting silence to keep RTP continuous");
                }
            }

            // Fill the whole elapsed span, not a fixed chunk, so the RTP timeline advances
            // at exactly wall-clock rate and never runs fast or slow across a gap.
            EmitSilence(CaptureGapFiller.GapMilliseconds(delta, Stopwatch.Frequency), format);
            Interlocked.Exchange(ref _lastCallbackQpc, now);
        }
    }

    private void EmitSilence(double gapMilliseconds, AudioFormat format)
    {
        var silence = CaptureGapFiller.CreateSilence(format, gapMilliseconds);
        if (silence.Length == 0)
        {
            return;
        }

        FrameAvailable?.Invoke(
            this,
            new AudioFrame(silence, format, DateTime.UtcNow.Ticks));
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

    /// <summary>
    /// <see cref="WasapiLoopbackCapture"/> with a caller-chosen client buffer. NAudio only
    /// exposes the buffer length on <see cref="WasapiCapture"/>, so loopback re-adds the
    /// stream flag itself.
    /// </summary>
    private sealed class ShortBufferLoopbackCapture : WasapiCapture
    {
        public ShortBufferLoopbackCapture(MMDevice device, int bufferMilliseconds)
            : base(device, useEventSync: false, audioBufferMillisecondsLength: bufferMilliseconds)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
