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
                ToolTipText = $"IP Address: {host.IPAddresses.FirstOrDefault()}"
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

        private static void PrintDeviceInfo(DeviceInfo device)
        {
            Console.WriteLine($"Device Name: {device.DeviceName}");
            Console.WriteLine($"Display Name: {device.DisplayName}");
            Console.WriteLine($"IP Address: {device.IPAddress}");
            Console.WriteLine($"Port: {device.Port}");
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
