#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinStream.Core.Streaming;
using WinStream.Network;

namespace WinStream.ViewModels;

/// <summary>Status tone for a device row, mapped to Fluent system fill brushes.</summary>
public enum DeviceStatusKind
{
    Neutral,
    Success,
    Caution,
    Error
}

/// <summary>
/// Presentation wrapper around a discovered <see cref="DeviceInfo"/> so the device
/// list can react to connection and status changes without rebuilding items.
/// </summary>
public sealed class DeviceViewModel : INotifyPropertyChanged
{
    private DeviceInfo _device;
    private bool _isConnected;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private DeviceStatusKind _statusKind = DeviceStatusKind.Neutral;

    public DeviceViewModel(DeviceInfo device) => _device = device;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviceInfo Device => _device;

    public string Key => BuildKey(_device);

    public string DisplayName => FriendlyName(_device.DisplayName);

    public string Subtitle => BuildSubtitle(_device);

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (Set(ref _isConnected, value))
            {
                Notify(nameof(ActionLabel));
                Notify(nameof(ActionButtonStyle));
                Notify(nameof(ConnectedBadgeVisibility));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                Notify(nameof(BusyVisibility));
                Notify(nameof(IsActionEnabled));
            }
        }
    }

    public string ActionLabel => _isConnected ? "Disconnect" : "Connect";

    /// <summary>Connecting is the primary action, so it gets accent styling; leaving isn't.</summary>
    public Style? ActionButtonStyle =>
        _isConnected ? null : LookupStyle("AccentButtonStyle");

    public bool IsActionEnabled => !_isBusy;

    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConnectedBadgeVisibility =>
        _isConnected ? Visibility.Visible : Visibility.Collapsed;

    public string StatusMessage => _statusMessage;

    public Visibility StatusVisibility =>
        string.IsNullOrWhiteSpace(_statusMessage) ? Visibility.Collapsed : Visibility.Visible;

    public Brush StatusBrush => ResolveBrush(_statusKind);

    public void SetStatus(string message, DeviceStatusKind kind)
    {
        _statusMessage = message ?? string.Empty;
        _statusKind = kind;
        Notify(nameof(StatusMessage));
        Notify(nameof(StatusVisibility));
        Notify(nameof(StatusBrush));
    }

    public void ClearStatus() => SetStatus(string.Empty, DeviceStatusKind.Neutral);

    /// <summary>Applies a freshly discovered record to this row, keeping UI state intact.</summary>
    public void Update(DeviceInfo device)
    {
        _device = device;
        Notify(nameof(DisplayName));
        Notify(nameof(Subtitle));
    }

    public static string BuildKey(DeviceInfo device) =>
        string.IsNullOrWhiteSpace(device.DeviceID)
            ? $"{device.IPAddress}:{device.Port}"
            : device.DeviceID;

    public bool MatchesFilter(string filter) =>
        DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (_device.IPAddress?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (_device.Model?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// RAOP advertises instances as <c>&lt;MAC&gt;@&lt;Name&gt;</c>; users only care about the name.
    /// </summary>
    private static string FriendlyName(string? advertisedName)
    {
        if (string.IsNullOrWhiteSpace(advertisedName))
        {
            return "Unknown device";
        }

        var separator = advertisedName.IndexOf('@');
        if (separator <= 0 || separator == advertisedName.Length - 1)
        {
            return advertisedName;
        }

        return IsMacAddressPrefix(advertisedName, separator)
            ? advertisedName[(separator + 1)..]
            : advertisedName;
    }

    private static bool IsMacAddressPrefix(string value, int length)
    {
        if (length != 12)
        {
            return false;
        }

        for (var i = 0; i < length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildSubtitle(DeviceInfo device)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(device.Model))
        {
            parts.Add(device.Model);
        }

        if (!string.IsNullOrWhiteSpace(device.IPAddress))
        {
            parts.Add(device.IPAddress);
        }

        parts.Add(DescribeProtocol(device));
        return string.Join("  •  ", parts);
    }

    private static string DescribeProtocol(DeviceInfo device)
    {
        if (AirPlayCapability.SupportsAirPlay2(
                !string.IsNullOrWhiteSpace(device.PublicCUAirPlayPairingIdentity),
                device.Features,
                device.AirPlayVersion))
        {
            return "AirPlay 2";
        }

        return AirPlayCapability.SupportsClassicRaop(device.EncryptionTypes)
            ? "AirPlay"
            : "Unrecognized protocol";
    }

    private static Style? LookupStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;

    private static Brush ResolveBrush(DeviceStatusKind kind)
    {
        var key = kind switch
        {
            DeviceStatusKind.Success => "SystemFillColorSuccessBrush",
            DeviceStatusKind.Caution => "SystemFillColorCautionBrush",
            DeviceStatusKind.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush"
        };

        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(propertyName);
        return true;
    }

    private void Notify(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
