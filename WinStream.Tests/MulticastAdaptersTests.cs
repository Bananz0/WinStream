using System.Net.NetworkInformation;
using WinStream.Network;

namespace WinStream.Tests;

public class MulticastAdaptersTests
{
    [Theory]
    [InlineData(OperationalStatus.Up, true, NetworkInterfaceType.Ethernet, true, true)]
    [InlineData(OperationalStatus.Down, true, NetworkInterfaceType.Ethernet, true, false)]
    [InlineData(OperationalStatus.Up, false, NetworkInterfaceType.Ethernet, true, false)]
    [InlineData(OperationalStatus.Up, true, NetworkInterfaceType.Loopback, true, false)]
    [InlineData(OperationalStatus.Up, true, NetworkInterfaceType.Tunnel, true, false)]
    [InlineData(OperationalStatus.Up, true, NetworkInterfaceType.Wireless80211, false, false)]
    public void Matches_filters_unusable_adapters(
        OperationalStatus status,
        bool multicast,
        NetworkInterfaceType type,
        bool hasIpv4,
        bool expected)
    {
        Assert.Equal(expected, MulticastAdapters.Matches(status, multicast, type, hasIpv4));
    }
}
