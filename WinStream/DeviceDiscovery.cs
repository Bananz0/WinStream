using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Zeroconf;

namespace WinStream.Network
{
    public static class DeviceDiscovery
    {
        public static async Task<List<DeviceInfo>> DiscoverDevicesAsync()
        {
            var results = await ZeroconfResolver.ResolveAsync("_raop._tcp.local.", TimeSpan.FromSeconds(5));
            return results.Select(host => new DeviceInfo
            {
                DisplayName = host.DisplayName,
                IPAddress = host.IPAddress,
                Port = host.Services.FirstOrDefault().Value.Port, // Assuming service information contains the port
                ToolTipText = $"IP Address: {host.IPAddress}"
            }).ToList();
        }
    }
}
