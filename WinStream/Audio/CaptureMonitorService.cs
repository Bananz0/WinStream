#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core;
using WinStream.Core.Audio;
using WinStream.Core.Persistence;
using WinStream.Core.Streaming;

namespace WinStream.Audio;

/// <summary>
/// Owns loopback capture for settings monitoring and later streaming phases.
/// Rebinds when the selected endpoint changes or WASAPI invalidates the device.
/// </summary>
public sealed class CaptureMonitorService : IAsyncDisposable
{
    private readonly AppSettingsService _settingsService;
    private readonly RenderEndpointEnumerator _endpointEnumerator = new();
    private readonly object _gate = new();
    private WasapiLoopbackSource? _source;
    private bool _disposed;
    private int _rebindAttempts;

    public CaptureMonitorService(AppSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public event EventHandler? StateChanged;

    private AppSettings Settings => _settingsService.Settings;

    public bool IsMonitoring => Settings.MonitorCapture;

    public string? SelectedEndpointId => Settings.SelectedRenderDeviceId;

    public bool IsCapturing => _source?.IsCapturing == true;

    public double CurrentRms => _source?.CurrentRms ?? 0;

    public bool IsSilent => _source?.IsSilent ?? true;

    public AudioFormat? Format => _source?.Format;

    public string? ActiveEndpointId => _source?.EndpointId;

    /// <summary>
    /// Measured capture contribution for Extreme honesty, or the frozen 50 ms constant
    /// when the event-driven experiment is off / not warmed up.
    /// </summary>
    public int CaptureContributionMilliseconds
    {
        get
        {
            var source = _source;
            return ExtremeCaptureExperiment.ResolveContributionMilliseconds(
                useEventDrivenCapture: source?.UseEventDrivenCapture == true,
                hasMeasuredContribution: source?.HasMeasuredContribution == true,
                measuredContributionMilliseconds: source?.MeasuredContributionMilliseconds ?? 0,
                frozenPollMilliseconds: WasapiLoopbackSource.CaptureBufferMilliseconds);
        }
    }

    public IReadOnlyList<RenderEndpointInfo> ListEndpoints() =>
        _endpointEnumerator.ListActiveRenderEndpoints();

    public async Task SetSelectedEndpointAsync(string? endpointId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settingsService.Update(settings => settings.SelectedRenderDeviceId = endpointId);

        if (_source is null)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await _source.RebindAsync(endpointId, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetMonitoringAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settingsService.Update(settings => settings.MonitorCapture = enabled);

        if (enabled)
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SyncExtremeCaptureExperimentAsync(cancellationToken).ConfigureAwait(false);

        WasapiLoopbackSource source;
        lock (_gate)
        {
            _source ??= CreateSource();
            source = _source;
        }

        if (!source.IsCapturing)
        {
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Flips Extreme's event-driven experiment on the existing loopback instance.
    /// Keeps the same <see cref="IAudioSource"/> reference so a live orchestrator
    /// subscription is not left pointing at a disposed capture.
    /// </summary>
    public async Task SyncExtremeCaptureExperimentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wantEventDriven = ExtremeCaptureExperiment.WantsEventDriven(
            Settings.ExtremeEventDrivenCapture,
            Settings.PlaybackResponsiveness);
        WasapiLoopbackSource existing;
        lock (_gate)
        {
            if (_source is null)
            {
                return;
            }

            if (_source.UseEventDrivenCapture == wantEventDriven)
            {
                return;
            }

            existing = _source;
        }

        var wasCapturing = existing.IsCapturing;
        await existing.StopAsync(cancellationToken).ConfigureAwait(false);
        existing.UseEventDrivenCapture = wantEventDriven;
        if (wasCapturing || Settings.MonitorCapture)
        {
            await existing.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WasapiLoopbackSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is not null)
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioSource? GetSourceForStreaming() => _source;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WasapiLoopbackSource? source;
        lock (_gate)
        {
            source = _source;
            _source = null;
        }

        if (source is not null)
        {
            source.DeviceInvalidated -= OnDeviceInvalidated;
            source.CaptureFailed -= OnCaptureFailed;
            await source.DisposeAsync().ConfigureAwait(false);
        }

        _endpointEnumerator.Dispose();
    }

    private WasapiLoopbackSource CreateSource()
    {
        var source = new WasapiLoopbackSource
        {
            PreferredEndpointId = Settings.SelectedRenderDeviceId,
            UseEventDrivenCapture = ExtremeCaptureExperiment.WantsEventDriven(
                Settings.ExtremeEventDrivenCapture,
                Settings.PlaybackResponsiveness)
        };
        source.DeviceInvalidated += OnDeviceInvalidated;
        source.CaptureFailed += OnCaptureFailed;
        return source;
    }

    private async void OnDeviceInvalidated(object? sender, EventArgs e)
    {
        if (_disposed || _rebindAttempts > 3)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _rebindAttempts++;
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (_source is not null && Settings.MonitorCapture)
            {
                await _source.RebindAsync(Settings.SelectedRenderDeviceId).ConfigureAwait(false);
                _rebindAttempts = 0;
            }
        }
        catch
        {
            // Surface via StateChanged; later phases map this to Reconnecting.
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaptureFailed(object? sender, Exception e)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
