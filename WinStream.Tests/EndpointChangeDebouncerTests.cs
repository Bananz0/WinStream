using WinStream.Core.Audio;

namespace WinStream.Tests;

public class EndpointChangeDebouncerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_notification_after_never_rebinding_should_rebind()
    {
        // DateTimeOffset.MinValue stands in for "no rebind has happened yet."
        Assert.True(EndpointChangeDebouncer.ShouldRebind(DateTimeOffset.MinValue, Epoch));
    }

    [Fact]
    public void A_second_notification_immediately_after_is_debounced()
    {
        var now = Epoch + TimeSpan.FromMilliseconds(5);

        Assert.False(EndpointChangeDebouncer.ShouldRebind(Epoch, now));
    }

    [Fact]
    public void A_notification_just_inside_the_window_is_debounced()
    {
        var now = Epoch + EndpointChangeDebouncer.DebounceWindow - TimeSpan.FromMilliseconds(1);

        Assert.False(EndpointChangeDebouncer.ShouldRebind(Epoch, now));
    }

    [Fact]
    public void A_notification_exactly_at_the_window_boundary_should_rebind()
    {
        var now = Epoch + EndpointChangeDebouncer.DebounceWindow;

        Assert.True(EndpointChangeDebouncer.ShouldRebind(Epoch, now));
    }

    [Fact]
    public void A_notification_well_after_the_window_should_rebind()
    {
        var now = Epoch + TimeSpan.FromSeconds(5);

        Assert.True(EndpointChangeDebouncer.ShouldRebind(Epoch, now));
    }

    [Fact]
    public void Multiple_role_notifications_for_one_physical_change_rebind_only_once()
    {
        var lastRebind = DateTimeOffset.MinValue;
        var rebindCount = 0;

        // Simulates eConsole, eMultimedia, eCommunications firing within ~50ms of
        // each other for a single default-device switch.
        foreach (var offset in new[] { 0, 10, 45 })
        {
            var now = Epoch + TimeSpan.FromMilliseconds(offset);
            if (EndpointChangeDebouncer.ShouldRebind(lastRebind, now))
            {
                rebindCount++;
                lastRebind = now;
            }
        }

        Assert.Equal(1, rebindCount);
    }
}
