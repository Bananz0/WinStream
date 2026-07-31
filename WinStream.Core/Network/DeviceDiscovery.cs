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
                var adapters = GetMulticastCapableAdapters();
                var raopResults = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken, netInterfacesToSendRequestOn: adapters);
                var airplayResults = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken, netInterfacesToSendRequestOn: adapters);

                var currentDevices = raopResults.Select(host => new DeviceInfo
                {
                    DisplayName = ExtractDeviceName(host, airplayResults),
                    IPAddress = host.IPAddresses.FirstOrDefault(),
                    Port = host.Services.FirstOrDefault().Value.Port,
                    ToolTipText = $"IP Address: {host.IPAddresses.FirstOrDefault()}",
                    Manufacturer = GetTxtRecordValue(host, "manufacturer"),
                    Model = GetTxtRecordValue(host, "model"),
                    FirmwareVersion = GetTxtRecordValue(host, "fv"),
                    OSVersion = GetTxtRecordValue(host, "osvers"),
                    BluetoothAddress = GetTxtRecordValue(host, "btaddr"),
                    DeviceID = GetTxtRecordValue(host, "deviceid"),
                    ProtocolVersion = GetTxtRecordValue(host, "protovers"),
                    AirPlayVersion = GetTxtRecordValue(host, "srcvers"),
                    SerialNumber = GetTxtRecordValue(host, "serialNumber"),
                    PublicCUAirPlayPairingIdentity = GetTxtRecordValue(host, "pi"),
                    PublicCUSystemPairingIdentity = GetTxtRecordValue(host, "psi"),
                    PublicKey = GetTxtRecordValue(host, "pk"),
                    EncryptionTypes = GetTxtRecordValue(host, "et"),
                    HouseholdID = GetTxtRecordValue(host, "hmid"),
                    GroupUUID = GetTxtRecordValue(host, "gid"),
                    IsGroupLeader = TryParseBoolean(GetTxtRecordValue(host, "igl")),
                    RequiredSenderFeatures = TryParseLong(GetTxtRecordValue(host, "rsf")),
                    SystemFlags = TryParseLong(GetTxtRecordValue(host, "flags")),
                    FeaturesRaw = GetTxtRecordValue(host, "features"),
                    Features = WinStream.Core.Streaming.AirPlayCapability.ParseFeatures(
                        GetTxtRecordValue(host, "features"))
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

        /// <summary>
        /// Zeroconf throws NetworkInformationException (10043) when an adapter has no
        /// IPv4 stack, so only hand it adapters that can actually carry mDNS.
        /// </summary>
        private static System.Net.NetworkInformation.NetworkInterface[] GetMulticastCapableAdapters()
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsableAdapter)
                .ToArray();
        }

        private static bool IsUsableAdapter(System.Net.NetworkInformation.NetworkInterface adapter)
        {
            if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                !adapter.SupportsMulticast ||
                adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            {
                return false;
            }

            try
            {
                var properties = adapter.GetIPProperties();
                properties.GetIPv4Properties();
                return properties.UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static string GetTxtRecordValue(IZeroconfHost host, string key)
        {
            foreach (var service in host.Services.Values)
            {
                if (service.Properties == null) continue;

                foreach (var record in service.Properties)
                {
                    if (record.TryGetValue(key, out var value))
                    {
                        return value;
                    }
                }
            }
            return string.Empty;
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
