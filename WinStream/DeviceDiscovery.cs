using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Zeroconf;

namespace WinStream.Network
{
    public static class DeviceDiscovery
    {
        private static readonly Dictionary<string, DeviceInfo> Devices = new();
        private static readonly Dictionary<string, int> DeviceMissCounts = new();

        public static async Task<List<DeviceInfo>> DiscoverDevicesAsync()
        {
            var raopResults = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", TimeSpan.FromSeconds(5));
            var airplayResults = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", TimeSpan.FromSeconds(5));

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

            var currentDeviceAddresses = currentDevices.Select(d => d.IPAddress).ToHashSet();

            // Reset miss counts for devices found in this scan
            foreach (var device in currentDevices)
            {
                if (!Devices.ContainsKey(device.IPAddress))
                {
                    Devices[device.IPAddress] = device;
                    PrintDeviceInfo(device); // Print device info when it's first discovered
                }
                DeviceMissCounts[device.IPAddress] = 0; // Reset miss count
            }

            // Increment miss counts for devices not found in this scan
            foreach (var deviceIp in Devices.Keys.ToList())
            {
                if (!currentDeviceAddresses.Contains(deviceIp))
                {
                    DeviceMissCounts[deviceIp]++;
                    // Remove device if it has missed 3 scans
                    if (DeviceMissCounts[deviceIp] >= 3)
                    {
                        Devices.Remove(deviceIp);
                        DeviceMissCounts.Remove(deviceIp);
                    }
                }
            }

            return Devices.Values.ToList();
        }

        private static string GetTxtRecordValue(IZeroconfHost host, string key)
        {
            // Iterate over services and their TXT records to find the desired key
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
            // Find the matching AirPlay host based on the IP address
            var airplayHost = airplayResults.FirstOrDefault(h => h.IPAddresses.Contains(raopHost.IPAddresses.FirstOrDefault()));

            // Extract the device name from the AirPlay host's display name
            return airplayHost?.DisplayName.Split('@').FirstOrDefault() ?? raopHost.DisplayName;
        }
    }
}
