#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zeroconf;

namespace WinStream.Core.Network;

/// <summary>Discovers WinStream Link companions (_winstream-link._udp) — never merges into AirPlay.</summary>
public static class LinkDeviceDiscovery
{
    public const string ServiceType = "_winstream-link._udp.local.";

    public static async Task<List<LinkDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var adapters = MulticastAdapters.Usable();
            var results = await ZeroconfResolver.ResolveAsync(
                ServiceType,
                TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken,
                netInterfacesToSendRequestOn: adapters);

            return results.Select(host =>
            {
                var txt = MergeTxt(host);
                return new LinkDeviceInfo
                {
                    DisplayName = FirstValue(txt, "name") is { Length: > 0 } n
                        ? n
                        : host.DisplayName,
                    IPAddress = host.IPAddresses.FirstOrDefault(),
                    MediaPort = host.Services.FirstOrDefault().Value?.Port
                        ?? LinkDefaults.MediaPort,
                    Version = FirstValue(txt, "ver"),
                    Format = FirstValue(txt, "fmt"),
                    SampleRate = FirstValue(txt, "rate")
                };
            }).ToList();
        }
        catch (OperationCanceledException)
        {
            return new List<LinkDeviceInfo>();
        }
    }

    private static Dictionary<string, string> MergeTxt(IZeroconfHost host)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (host?.Services == null)
        {
            return merged;
        }

        foreach (var service in host.Services.Values)
        {
            if (service.Properties == null)
            {
                continue;
            }

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

        return merged;
    }

    private static string FirstValue(Dictionary<string, string> txt, string key) =>
        txt.TryGetValue(key, out var value) ? value : string.Empty;
}

public sealed class LinkDeviceInfo
{
    public string DisplayName { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public int MediaPort { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string SampleRate { get; set; } = string.Empty;

    public string Key =>
        string.IsNullOrWhiteSpace(IPAddress)
            ? DisplayName
            : $"{IPAddress}:{MediaPort}";
}
