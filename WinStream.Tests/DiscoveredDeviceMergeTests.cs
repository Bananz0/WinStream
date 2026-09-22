using WinStream.Core.Network;

namespace WinStream.Tests;

public class DiscoveredDeviceMergeTests
{
    private static DeviceInfo Full() => new()
    {
        DisplayName = "living-room",
        IPAddress = "192.0.2.10",
        Port = 7000,
        Model = "MacBookPro18,1",
        DeviceID = "AA:BB:CC:DD:EE:FF",
        PublicKey = "deadbeef",
        EncryptionTypes = "0,3,5",
        FeaturesRaw = "0x12345678,0x87654321",
        Features = 0x12345678,
        StatusFlagsRaw = "0x284",
        StatusFlags = 0x284
    };

    [Fact]
    public void A_pass_that_missed_airplay_keeps_the_password_requirement()
    {
        var previous = Full();
        var partial = new DeviceInfo
        {
            DisplayName = "living-room",
            IPAddress = "192.168.1.10",
            Port = 7000
        };

        var merged = DiscoveredDeviceMerge.CarryForward(previous, partial);

        Assert.True(merged.RequiresPassword);
        Assert.Equal("0x284", merged.StatusFlagsRaw);
        Assert.Equal("AA:BB:CC:DD:EE:FF", merged.DeviceID);
        Assert.Equal("MacBookPro18,1", merged.Model);
    }

    [Fact]
    public void The_newest_address_always_wins()
    {
        var merged = DiscoveredDeviceMerge.CarryForward(
            Full(),
            new DeviceInfo { DisplayName = "living-room", IPAddress = "192.168.1.55", Port = 7000 });

        Assert.Equal("192.168.1.55", merged.IPAddress);
    }

    [Fact]
    public void A_receiver_that_turned_its_password_off_is_not_overridden()
    {
        var current = Full();
        current.StatusFlagsRaw = "0x204";
        current.StatusFlags = 0x204;

        var merged = DiscoveredDeviceMerge.CarryForward(Full(), current);

        Assert.False(merged.RequiresPassword);
    }

    [Fact]
    public void A_first_sighting_is_kept_as_is()
    {
        var current = Full();

        Assert.Same(current, DiscoveredDeviceMerge.CarryForward(null, current));
    }

    [Fact]
    public void An_address_keyed_leftover_for_the_same_name_is_a_duplicate()
    {
        var leftover = new DeviceInfo
        {
            DisplayName = "living-room",
            IPAddress = "192.168.1.10",
            Port = 7000
        };

        Assert.True(DiscoveredDeviceMerge.IsStaleDuplicate(leftover, Full()));
    }

    [Fact]
    public void An_address_keyed_leftover_at_the_same_address_is_a_duplicate()
    {
        var leftover = new DeviceInfo { IPAddress = "192.0.2.10", Port = 7000 };

        Assert.True(DiscoveredDeviceMerge.IsStaleDuplicate(leftover, Full()));
    }

    [Fact]
    public void Two_identified_receivers_are_never_duplicates()
    {
        var other = Full();
        other.DeviceID = "42:AE:E9:D5:A5:60";
        other.DisplayName = "living-room";

        Assert.False(DiscoveredDeviceMerge.IsStaleDuplicate(other, Full()));
    }

    [Fact]
    public void A_different_receiver_on_another_address_is_not_a_duplicate()
    {
        var other = new DeviceInfo
        {
            DisplayName = "office-speaker",
            IPAddress = "192.0.2.20",
            Port = 7000
        };

        Assert.False(DiscoveredDeviceMerge.IsStaleDuplicate(other, Full()));
    }
}
