using WinStream.Core.Network;

namespace WinStream.Tests;

public class LinkDeviceDiscoveryIsolationTests
{
    [Fact]
    public void Link_service_type_is_not_airplay_or_raop()
    {
        Assert.Equal("_winstream-link._udp.local.", LinkDeviceDiscovery.ServiceType);
        Assert.DoesNotContain("airplay", LinkDeviceDiscovery.ServiceType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raop", LinkDeviceDiscovery.ServiceType, StringComparison.OrdinalIgnoreCase);
    }
}
