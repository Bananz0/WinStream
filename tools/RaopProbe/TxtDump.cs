using WinStream.Core.Network;
using Zeroconf;

namespace WinStream.Tools.RaopProbe;

/// <summary>
/// Dumps every mDNS TXT key for both AirPlay service types. Capability routing
/// depends on which record carries which key, so this shows the raw truth.
/// </summary>
public static class TxtDump
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        foreach (var serviceType in new[] { "_raop._tcp.local.", "_airplay._tcp.local." })
        {
            Console.WriteLine($"== {serviceType} ==");
            var hosts = await ZeroconfResolver.ResolveAsync(
                serviceType,
                TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken,
                netInterfacesToSendRequestOn: MulticastAdapters.Usable());

            if (hosts.Count == 0)
            {
                Console.WriteLine("  (none)");
                continue;
            }

            foreach (var host in hosts)
            {
                Console.WriteLine($"  host {host.DisplayName} {string.Join(",", host.IPAddresses)}");
                foreach (var (name, service) in host.Services)
                {
                    Console.WriteLine($"    service {name} port={service.Port}");
                    foreach (var propertySet in service.Properties)
                    {
                        foreach (var (key, value) in propertySet.OrderBy(p => p.Key))
                        {
                            var shown = value is { Length: > 48 }
                                ? $"{value[..48]}…(len={value.Length})"
                                : value;
                            Console.WriteLine($"      {key} = {shown}");
                        }
                    }
                }
            }
        }

        return 0;
    }
}
