using WinStream.Core.Network;

namespace WinStream.Tests;

public class DeviceRetentionTrackerTests
{
    private static DeviceInfo Device(string name, string address, string? deviceId = null) => new()
    {
        DisplayName = name,
        IPAddress = address,
        Port = 7000,
        DeviceID = deviceId ?? string.Empty
    };

    [Fact]
    public void A_first_sighting_is_tracked_and_returned()
    {
        var tracker = new DeviceRetentionTracker();

        var result = tracker.Merge([Device("living-room", "192.168.1.10")], _ => false);

        Assert.Single(result);
        Assert.Equal(1, tracker.KnownDeviceCount);
    }

    [Fact]
    public void A_receiver_missing_for_fewer_than_the_drop_threshold_stays_listed()
    {
        var tracker = new DeviceRetentionTracker();
        tracker.Merge([Device("living-room", "192.168.1.10")], _ => false);

        tracker.Merge([], _ => false);
        var stillListed = tracker.Merge([], _ => false);

        Assert.Single(stillListed);
        Assert.Equal(1, tracker.KnownDeviceCount);
    }

    [Fact]
    public void A_receiver_missing_three_consecutive_passes_drops_off()
    {
        var tracker = new DeviceRetentionTracker();
        tracker.Merge([Device("living-room", "192.168.1.10")], _ => false);

        tracker.Merge([], _ => false);
        tracker.Merge([], _ => false);
        tracker.Merge([], _ => false);

        Assert.Equal(0, tracker.KnownDeviceCount);
    }

    [Fact]
    public void A_streaming_receiver_is_kept_despite_a_missed_pass()
    {
        var tracker = new DeviceRetentionTracker();
        tracker.Merge([Device("living-room", "192.168.1.10")], _ => false);
        var key = ReceiverKey.For(Device("living-room", "192.168.1.10"));

        for (var i = 0; i < 5; i++)
        {
            tracker.Merge([], k => k == key);
        }

        Assert.Equal(1, tracker.KnownDeviceCount);
    }

    [Fact]
    public void A_reappearing_receiver_resets_its_miss_count()
    {
        var tracker = new DeviceRetentionTracker();
        var device = Device("living-room", "192.168.1.10");
        tracker.Merge([device], _ => false);

        tracker.Merge([], _ => false);
        tracker.Merge([], _ => false);
        tracker.Merge([device], _ => false);
        tracker.Merge([], _ => false);
        tracker.Merge([], _ => false);

        Assert.Equal(1, tracker.KnownDeviceCount);
    }

    [Fact]
    public void An_address_keyed_leftover_is_dropped_once_the_same_receiver_gets_an_identity()
    {
        var tracker = new DeviceRetentionTracker();
        tracker.Merge([Device("living-room", "192.168.1.10")], _ => false);

        var result = tracker.Merge(
            [Device("living-room", "192.168.1.10", deviceId: "AA:BB:CC:DD:EE:FF")],
            _ => false);

        Assert.Single(result);
        Assert.Equal(1, tracker.KnownDeviceCount);
    }
}
