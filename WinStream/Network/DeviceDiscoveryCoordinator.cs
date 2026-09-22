#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Network;

namespace WinStream.Network;

/// <summary>
/// Owns mDNS scanning; retention policy for absent receivers lives in
/// <see cref="DeviceRetentionTracker"/> so it can be unit-tested without WinUI.
/// </summary>
public sealed class DeviceDiscoveryCoordinator
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);

    private readonly DeviceRetentionTracker _tracker = new();

    public bool IsScanning { get; private set; }

    public int KnownDeviceCount => _tracker.KnownDeviceCount;

    /// <summary>
    /// Runs one scan and returns every receiver still considered present, or
    /// <see langword="null"/> when a scan is already in flight.
    /// </summary>
    /// <param name="keepDespiteMisses">
    /// Receivers (by key) that must stay listed even when a pass misses them —
    /// a streaming receiver should not vanish because one mDNS reply was lost.
    /// </param>
    public async Task<IReadOnlyList<DeviceInfo>?> ScanAsync(
        Func<string, bool> keepDespiteMisses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keepDespiteMisses);
        if (IsScanning)
        {
            return null;
        }

        IsScanning = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ScanTimeout);
            var found = await DeviceDiscovery.DiscoverDevicesAsync(timeout.Token)
                .ConfigureAwait(true);
            return _tracker.Merge(found, keepDespiteMisses);
        }
        finally
        {
            IsScanning = false;
        }
    }
}
