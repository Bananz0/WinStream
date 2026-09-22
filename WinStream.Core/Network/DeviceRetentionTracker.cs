#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using WinStream.Core.Logging;

namespace WinStream.Core.Network;

/// <summary>
/// Merges one mDNS pass into the running device list and decides how long an
/// absent receiver stays listed, so discovery and the device list can never
/// disagree about what is present. WinUI-free so it can be driven from tests
/// without a scan.
/// </summary>
public sealed class DeviceRetentionTracker
{
    /// <summary>A receiver may miss this many consecutive passes before it drops off.</summary>
    private const int MissesBeforeDrop = 3;

    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);

    public int KnownDeviceCount => _tracked.Count;

    /// <summary>
    /// Folds one scan's results into the tracked set and returns every receiver
    /// still considered present.
    /// </summary>
    /// <param name="keepDespiteMisses">
    /// Receivers (by key) that must stay listed even when a pass misses them —
    /// a streaming receiver should not vanish because one mDNS reply was lost.
    /// </param>
    public IReadOnlyList<DeviceInfo> Merge(
        IReadOnlyList<DeviceInfo> found,
        Func<string, bool> keepDespiteMisses)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(keepDespiteMisses);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in found)
        {
            var key = ReceiverKey.For(device);
            seen.Add(key);

            if (_tracked.TryGetValue(key, out var tracked))
            {
                // A pass that misses _airplay carries no flags; keeping what the
                // receiver already told us stops the password badge from flickering.
                tracked.Device = DiscoveredDeviceMerge.CarryForward(tracked.Device, device);
                tracked.Misses = 0;
                continue;
            }

            _tracked[key] = new Tracked(device);
            AppLog.Info("discovery", $"Discovered receiver model={device.Model}; port={device.Port}");
            DropStaleDuplicates(key, device);
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

    /// <summary>
    /// Removes address-keyed leftovers for a receiver that now advertises an identity,
    /// so one Mac stops occupying two rows once its <c>deviceid</c> is known again.
    /// </summary>
    private void DropStaleDuplicates(string key, DeviceInfo device)
    {
        foreach (var (existingKey, existing) in _tracked.ToList())
        {
            if (!string.Equals(existingKey, key, StringComparison.Ordinal) &&
                DiscoveredDeviceMerge.IsStaleDuplicate(existing.Device, device))
            {
                _tracked.Remove(existingKey);
                AppLog.Info(
                    "discovery",
                    $"Dropped duplicate listing for model={device.Model}; port={device.Port}");
            }
        }
    }

    private sealed class Tracked(DeviceInfo device)
    {
        public DeviceInfo Device { get; set; } = device;

        public int Misses { get; set; }
    }
}
