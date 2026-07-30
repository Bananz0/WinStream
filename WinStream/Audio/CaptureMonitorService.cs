#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Audio;
using WinStream.Core.Persistence;

namespace WinStream.Audio;

/// <summary>
/// Owns loopback capture for settings monitoring and later streaming phases.
/// Rebinds when the selected endpoint changes or WASAPI invalidates the device.
/// </summary>
public sealed class CaptureMonitorService : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly RenderEndpointEnumerator _endpointEnumerator = new();
    private readonly object _gate = new();
    private WasapiLoopbackSource? _source;
    private AppSettings _settings;
    private bool _disposed;
    private int _rebindAttempts;

    public CaptureMonitorService(SettingsStore? settingsStore = null)
    {
        _settingsStore = settingsStore ?? new SettingsStore();
        _settings = _settingsStore.Load();
    }

    public event EventHandler? StateChanged;

    public AppSettings Settings => _settings;

    public bool IsCapturing => _source?.IsCapturing == true;

    public double CurrentRms => _source?.CurrentRms ?? 0;

    public bool IsSilent => _source?.IsSilent ?? true;

    public AudioFormat? Format => _source?.Format;

    public string? ActiveEndpointId => _source?.EndpointId;

    public IReadOnlyList<RenderEndpointInfo> ListEndpoints() =>
        _endpointEnumerator.ListActiveRenderEndpoints();

    public async Task SetSelectedEndpointAsync(string? endpointId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SelectedRenderDeviceId = endpointId;
        _settingsStore.Save(_settings);

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
        _settings.MonitorCapture = enabled;
        _settingsStore.Save(_settings);

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
            PreferredEndpointId = _settings.SelectedRenderDeviceId
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
            if (_source is not null && _settings.MonitorCapture)
            {
                await _source.RebindAsync(_settings.SelectedRenderDeviceId).ConfigureAwait(false);
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
