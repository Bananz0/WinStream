#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zeroconf;

namespace WinStream.Core.Network
{
    public static class DeviceDiscovery
    {
        /// <summary>
        /// Resolves the receivers visible in this scan. Retention across passes is the
        /// caller's policy so discovery and the device list cannot disagree about what
        /// is still present.
        /// </summary>
        public static async Task<List<DeviceInfo>> DiscoverDevicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var adapters = MulticastAdapters.Usable();
                var raopResults = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken, netInterfacesToSendRequestOn: adapters);
                var airplayResults = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken, netInterfacesToSendRequestOn: adapters);

                return raopResults.Select(host =>
                {
                    // _raop advertises ft/vs/am; features/srcvers/pi only exist on
                    // _airplay, so AirPlay 2 detection needs both records merged.
                    var txt = MergeTxtRecords(host, FindAirPlayHost(host, airplayResults));
                    var features = FirstValue(txt, "features", "ft");

                    return new DeviceInfo
                    {
                        DisplayName = ExtractDeviceName(host, airplayResults),
                        IPAddress = host.IPAddresses.FirstOrDefault(),
                        Port = host.Services.FirstOrDefault().Value.Port,
                        Manufacturer = FirstValue(txt, "manufacturer"),
                        Model = FirstValue(txt, "model", "am"),
                        DeviceID = FirstValue(txt, "deviceid"),
                        ProtocolVersion = FirstValue(txt, "protovers"),
                        AirPlayVersion = FirstValue(txt, "srcvers", "vs"),
                        PublicCUAirPlayPairingIdentity = FirstValue(txt, "pi"),
                        PublicKey = FirstValue(txt, "pk"),
                        EncryptionTypes = FirstValue(txt, "et"),
                        FeaturesRaw = features,
                        Features = WinStream.Core.Streaming.AirPlayCapability.ParseFeatures(features)
                    };
                }).ToList();
            }
            catch (OperationCanceledException)
            {
                WinStream.Core.Logging.AppLog.Info("discovery", "Device discovery canceled or timed out.");
                return new List<DeviceInfo>();
            }
        }

        private static Dictionary<string, string> MergeTxtRecords(params IZeroconfHost[] hosts)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var host in hosts)
            {
                if (host?.Services == null) continue;

                foreach (var service in host.Services.Values)
                {
                    if (service.Properties == null) continue;

                    foreach (var record in service.Properties)
                    {
                        foreach (var pair in record)
                        {
                            if (!string.IsNullOrWhiteSpace(pair.Value))
                            {
                                merged[pair.Key] = pair.Value;
                            }
                        }
                    }
                }
            }

            return merged;
        }

        /// <summary>Returns the first populated value across the given TXT key aliases.</summary>
        private static string FirstValue(Dictionary<string, string> txt, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (txt.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static IZeroconfHost FindAirPlayHost(
            IZeroconfHost raopHost,
            IReadOnlyList<IZeroconfHost> airplayResults)
        {
            var match = airplayResults.FirstOrDefault(h =>
                h.IPAddresses.Any(address => raopHost.IPAddresses.Contains(address)));
            if (match != null)
            {
                return match;
            }

            // Some receivers publish the two services on different interfaces.
            var raopName = StripMacPrefix(raopHost.DisplayName);
            return airplayResults.FirstOrDefault(h =>
                string.Equals(StripMacPrefix(h.DisplayName), raopName, StringComparison.OrdinalIgnoreCase));
        }

        private static string StripMacPrefix(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return string.Empty;
            }

            var separator = displayName.IndexOf('@');
            return separator >= 0 ? displayName.Substring(separator + 1) : displayName;
        }

        private static string ExtractDeviceName(IZeroconfHost raopHost, IReadOnlyList<IZeroconfHost> airplayResults)
        {
            var airplayHost = airplayResults.FirstOrDefault(h => h.IPAddresses.Contains(raopHost.IPAddresses.FirstOrDefault()));
            return airplayHost?.DisplayName.Split('@').FirstOrDefault() ?? raopHost.DisplayName;
        }
    }
}
