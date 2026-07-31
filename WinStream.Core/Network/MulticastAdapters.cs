using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinStream.Core.Network;

/// <summary>
/// Zeroconf throws NetworkInformationException (10043) when an adapter has no
/// IPv4 stack, so callers must only hand it adapters that can carry mDNS.
/// </summary>
public static class MulticastAdapters
{
    public static NetworkInterface[] Usable() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => Matches(
                adapter.OperationalStatus,
                adapter.SupportsMulticast,
                adapter.NetworkInterfaceType,
                HasIpv4(adapter)))
            .ToArray();

    /// <summary>Pure filter used by unit tests.</summary>
    internal static bool Matches(
        OperationalStatus status,
        bool supportsMulticast,
        NetworkInterfaceType type,
        bool hasIpv4) =>
        status == OperationalStatus.Up &&
        supportsMulticast &&
        type != NetworkInterfaceType.Loopback &&
        type != NetworkInterfaceType.Tunnel &&
        hasIpv4;

    private static bool HasIpv4(NetworkInterface adapter)
    {
        try
        {
            var properties = adapter.GetIPProperties();
            properties.GetIPv4Properties();
            return properties.UnicastAddresses.Any(address =>
                address.Address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
