#nullable enable

using System.Collections.Generic;

namespace WinStream.Tray;

/// <summary>Snapshot used to build the tray context menu on right-click.</summary>
internal sealed class TrayMenuState
{
    public static TrayMenuState Empty { get; } = new();

    /// <summary>Friendly name of the remembered receiver, when one exists.</summary>
    public string? LastReceiverName { get; init; }

    /// <summary>True when the remembered receiver is on the network and not already connected.</summary>
    public bool CanConnectLast { get; init; }

    /// <summary>True when at least one receiver is streaming.</summary>
    public bool CanDisconnect { get; init; }

    /// <summary>How many receivers are currently connected (drives Disconnect vs Disconnect all).</summary>
    public int ConnectedCount { get; init; }

    /// <summary>Discovered receivers for the Connect submenu.</summary>
    public IReadOnlyList<TrayDeviceItem> Devices { get; init; } = [];
}

/// <summary>One row in the Connect submenu.</summary>
internal sealed class TrayDeviceItem
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public bool IsConnected { get; init; }

    /// <summary>False while a connect/disconnect is already in flight for this row.</summary>
    public bool IsEnabled { get; init; } = true;
}
