#nullable enable

using System;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using WinStream.Core.Logging;

namespace WinStream.Streaming;

/// <summary>
/// Observes network and power events and raises a single RecoverRequested signal.
/// </summary>
public sealed class ResilienceMonitor : IDisposable
{
    private bool _disposed;

    public event EventHandler<string>? RecoverRequested;

    public ResilienceMonitor()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        AppLog.Info("network", e.IsAvailable ? "Network available." : "Network unavailable.");
        if (e.IsAvailable)
        {
            RecoverRequested?.Invoke(this, "network-available");
        }
        else
        {
            RecoverRequested?.Invoke(this, "network-lost");
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        AppLog.Info("network", "Network address changed.");
        RecoverRequested?.Invoke(this, "network-address-changed");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        AppLog.Info("power", $"Power mode: {e.Mode}");
        if (e.Mode == PowerModes.Resume)
        {
            RecoverRequested?.Invoke(this, "power-resume");
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            RecoverRequested?.Invoke(this, "power-suspend");
        }
    }
}
