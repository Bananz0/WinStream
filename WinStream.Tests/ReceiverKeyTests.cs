using WinStream.Core.Network;

namespace WinStream.Tests;

public class ReceiverKeyTests
{
    [Fact]
    public void Prefers_the_advertised_device_id()
    {
        var device = new DeviceInfo
        {
            DeviceID = "AA:BB:CC:DD:EE:FF",
            IPAddress = "10.0.0.5",
            Port = 7000
        };

        Assert.Equal("AA:BB:CC:DD:EE:FF", ReceiverKey.For(device));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_address_and_port_without_a_device_id(string? deviceId)
    {
        var device = new DeviceInfo
        {
            DeviceID = deviceId,
            IPAddress = "10.0.0.5",
            Port = 7000
        };

        Assert.Equal("10.0.0.5:7000", ReceiverKey.For(device));
    }

    [Fact]
    public void A_receiver_that_changed_address_keeps_its_key()
    {
        var before = new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.5", Port = 7000 };
        var after = new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.42", Port = 7000 };

        Assert.True(ReceiverKey.SameReceiver(before, after));
        Assert.True(ReceiverKey.Matches(ReceiverKey.For(before), after));
    }

    [Fact]
    public void Different_receivers_do_not_match()
    {
        var left = new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.5", Port = 7000 };
        var right = new DeviceInfo { DeviceID = "CC:DD", IPAddress = "10.0.0.5", Port = 7000 };

        Assert.False(ReceiverKey.SameReceiver(left, right));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_key_never_matches(string? key)
    {
        var device = new DeviceInfo { DeviceID = "AA:BB", IPAddress = "10.0.0.5", Port = 7000 };

        Assert.False(ReceiverKey.Matches(key, device));
    }
}
