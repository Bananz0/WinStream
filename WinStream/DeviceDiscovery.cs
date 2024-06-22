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
                Console.WriteLine("Discovery was canceled or timed out.");
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

        internal static async Task<List<DeviceInfo>> DiscoverDevicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var raopResults = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
                var airplayResults = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

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
                    HouseholdID = GetTxtRecordValue(host, "hmid"),
                    GroupUUID = GetTxtRecordValue(host, "gid"),
                    IsGroupLeader = TryParseBoolean(GetTxtRecordValue(host, "igl")),
                    RequiredSenderFeatures = TryParseLong(GetTxtRecordValue(host, "rsf")),
                    SystemFlags = TryParseLong(GetTxtRecordValue(host, "flags"))
                }).ToList();

                ProcessDiscoveredDevices(currentDevices);
                return Devices.Values.ToList();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Device discovery operation was canceled due to timeout.");
                return new List<DeviceInfo>(); // or handle accordingly
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
                    PrintDeviceInfo(device); // Print device info when it's first discovered
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

        private static void PrintDeviceInfo(DeviceInfo device)
        {
            Console.WriteLine($"Device Name: {device.DeviceName}");
            Console.WriteLine($"Display Name: {device.DisplayName}");
            Console.WriteLine($"IP Address: {device.IPAddress}");
            Console.WriteLine($"Port: {device.Port}");
            Console.WriteLine($"Manufacturer: {device.Manufacturer}");
            Console.WriteLine($"Model: {device.Model}");
            Console.WriteLine($"Firmware Version: {device.FirmwareVersion}");
            Console.WriteLine($"OS Version: {device.OSVersion}");
            Console.WriteLine($"Bluetooth Address: {device.BluetoothAddress}");
            Console.WriteLine($"Device ID: {device.DeviceID}");
            Console.WriteLine($"Protocol Version: {device.ProtocolVersion}");
            Console.WriteLine($"AirPlay Version: {device.AirPlayVersion}");
            Console.WriteLine($"Serial Number: {device.SerialNumber}");
            Console.WriteLine($"Public CU AirPlay Pairing Identity: {device.PublicCUAirPlayPairingIdentity}");
            Console.WriteLine($"Public CU System Pairing Identity: {device.PublicCUSystemPairingIdentity}");
            Console.WriteLine($"Public Key: {device.PublicKey}");
            Console.WriteLine($"Household ID: {device.HouseholdID}");
            Console.WriteLine($"Group UUID: {device.GroupUUID}");
            Console.WriteLine($"Is Group Leader: {device.IsGroupLeader}");
            Console.WriteLine($"Required Sender Features: {device.RequiredSenderFeatures}");
            Console.WriteLine($"System Flags: {device.SystemFlags}");
            Console.WriteLine($"Tooltip Text: {device.ToolTipText}");
            Console.WriteLine();
        }

        private static string ExtractDeviceName(IZeroconfHost raopHost, IReadOnlyList<IZeroconfHost> airplayResults)
        {
            var airplayHost = airplayResults.FirstOrDefault(h => h.IPAddresses.Contains(raopHost.IPAddresses.FirstOrDefault()));
            return airplayHost?.DisplayName.Split('@').FirstOrDefault() ?? raopHost.DisplayName;
        }
    }
}
