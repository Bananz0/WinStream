#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zeroconf;

namespace WinStream.Network
{
    public static class DeviceDiscovery
    {
        private static readonly Dictionary<string, DeviceInfo> Devices = new();
        private static readonly Dictionary<string, int> DeviceMissCounts = new();
        private static CancellationTokenSource _cts;

        public static event EventHandler<List<DeviceInfo>> DevicesUpdated;
        public static event EventHandler<bool> DiscoveryStatusChanged;

        public static void StartDiscovery()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                throw new InvalidOperationException("Discovery is already running.");
            }

            _cts = new CancellationTokenSource();
            Task.Run(() => StartDiscoveryAsync(_cts.Token));
        }

        private static async Task StartDiscoveryAsync(CancellationToken cancellationToken)
        {
            DiscoveryStatusChanged?.Invoke(null, true);

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var devices = await DiscoverDevicesAsync(linkedCts.Token);
                    DevicesUpdated?.Invoke(null, devices);
                    await Task.Delay(5000, linkedCts.Token); // Wait 5 seconds before next scan
                }
            }
            catch (OperationCanceledException)
            {
                WinStream.Core.Logging.AppLog.Info("discovery", "Background discovery canceled.");
            }
            finally
            {
                DiscoveryStatusChanged?.Invoke(null, false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        public static void StopDiscovery()
        {
            _cts?.Cancel();
        }

        public static async Task<List<DeviceInfo>> DiscoverDevicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var adapters = MulticastAdapters.Usable();
                var raopResults = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken, netInterfacesToSendRequestOn: adapters);
                var airplayResults = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken, netInterfacesToSendRequestOn: adapters);

                var currentDevices = raopResults.Select(host =>
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
                        ToolTipText = $"IP Address: {host.IPAddresses.FirstOrDefault()}",
                        Manufacturer = FirstValue(txt, "manufacturer"),
                        Model = FirstValue(txt, "model", "am"),
                        FirmwareVersion = FirstValue(txt, "fv"),
                        OSVersion = FirstValue(txt, "osvers"),
                        BluetoothAddress = FirstValue(txt, "btaddr"),
                        DeviceID = FirstValue(txt, "deviceid"),
                        ProtocolVersion = FirstValue(txt, "protovers"),
                        AirPlayVersion = FirstValue(txt, "srcvers", "vs"),
                        SerialNumber = FirstValue(txt, "serialNumber"),
                        PublicCUAirPlayPairingIdentity = FirstValue(txt, "pi"),
                        PublicCUSystemPairingIdentity = FirstValue(txt, "psi"),
                        PublicKey = FirstValue(txt, "pk"),
                        EncryptionTypes = FirstValue(txt, "et"),
                        HouseholdID = FirstValue(txt, "hmid"),
                        GroupUUID = FirstValue(txt, "gid"),
                        IsGroupLeader = TryParseBoolean(FirstValue(txt, "igl")),
                        RequiredSenderFeatures = TryParseLong(FirstValue(txt, "rsf")),
                        SystemFlags = TryParseLong(FirstValue(txt, "flags")),
                        FeaturesRaw = features,
                        Features = WinStream.Core.Streaming.AirPlayCapability.ParseFeatures(features)
                    };
                }).ToList();

                ProcessDiscoveredDevices(currentDevices);
                return Devices.Values.ToList();
            }
            catch (OperationCanceledException)
            {
                WinStream.Core.Logging.AppLog.Info("discovery", "Device discovery canceled or timed out.");
                return new List<DeviceInfo>();
            }
        }

        private static void ProcessDiscoveredDevices(List<DeviceInfo> currentDevices)
        {
            var currentDeviceAddresses = currentDevices.Select(d => d.IPAddress).ToHashSet();

            foreach (var device in currentDevices)
            {
                if (!Devices.ContainsKey(device.IPAddress))
                {
                    Devices[device.IPAddress] = device;
                    WinStream.Core.Logging.AppLog.Info(
                        "discovery",
                        $"Discovered receiver model={device.Model}; port={device.Port}");
                }
                DeviceMissCounts[device.IPAddress] = 0; // Reset miss count
            }

            foreach (var deviceIp in Devices.Keys.ToList())
            {
                if (!currentDeviceAddresses.Contains(deviceIp))
                {
                    DeviceMissCounts[deviceIp]++;
                    if (DeviceMissCounts[deviceIp] >= 3)
                    {
                        Devices.Remove(deviceIp);
                        DeviceMissCounts.Remove(deviceIp);
                    }
                }
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

        private static bool TryParseBoolean(string value)
        {
            return bool.TryParse(value, out var result) ? result : false;
        }

        private static long TryParseLong(string value)
        {
            return long.TryParse(value, out var result) ? result : 0;
        }

        private static string ExtractDeviceName(IZeroconfHost raopHost, IReadOnlyList<IZeroconfHost> airplayResults)
        {
            var airplayHost = airplayResults.FirstOrDefault(h => h.IPAddresses.Contains(raopHost.IPAddresses.FirstOrDefault()));
            return airplayHost?.DisplayName.Split('@').FirstOrDefault() ?? raopHost.DisplayName;
        }
    }
}
