#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Logging;
using WinStream.Core.Network;

namespace WinStream.Network;

/// <summary>
/// Owns mDNS scanning and the one policy for how long an absent receiver stays
/// listed, so discovery and the device list can never disagree about what is present.
/// </summary>
public sealed class DeviceDiscoveryCoordinator
{
    /// <summary>A receiver may miss this many consecutive passes before it drops off.</summary>
    private const int MissesBeforeDrop = 3;

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);

    public bool IsScanning { get; private set; }

    public int KnownDeviceCount => _tracked.Count;

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
            return Merge(found, keepDespiteMisses);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private IReadOnlyList<DeviceInfo> Merge(
        List<DeviceInfo> found,
        Func<string, bool> keepDespiteMisses)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in found)
        {
            var key = ReceiverKey.For(device);
            seen.Add(key);

            if (_tracked.TryGetValue(key, out var tracked))
            {
                tracked.Device = device;
                tracked.Misses = 0;
                continue;
            }

            _tracked[key] = new Tracked(device);
            AppLog.Info("discovery", $"Discovered receiver model={device.Model}; port={device.Port}");
        }

        foreach (var (key, tracked) in _tracked.ToList())
        {
            if (seen.Contains(key) || keepDespiteMisses(key))
            {
                tracked.Misses = 0;
                continue;
            }

            if (++tracked.Misses >= MissesBeforeDrop)
            {
                _tracked.Remove(key);
            }
        }

        return _tracked.Values.Select(tracked => tracked.Device).ToList();
    }

    private sealed class Tracked(DeviceInfo device)
    {
        public DeviceInfo Device { get; set; } = device;

        public int Misses { get; set; }
    }
}
