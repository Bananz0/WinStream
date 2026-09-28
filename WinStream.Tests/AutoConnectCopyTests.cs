using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Auto-connect copy must name the preferred receiver — otherwise the toggle looks
/// global while the behavior is per-device.
/// </summary>
public class AutoConnectCopyTests
{
    [Fact]
    public void On_description_names_the_receiver()
    {
        Assert.Contains("living-room", AutoConnectCopy.OnDescription("living-room"));
        Assert.Contains("Auto-connects to", AutoConnectCopy.OnDescription("living-room"));
    }

    [Fact]
    public void Off_description_still_names_the_preferred_receiver()
    {
        Assert.Contains("MacBook", AutoConnectCopy.OffDescription("MacBook"));
        Assert.Contains("Preferred:", AutoConnectCopy.OffDescription("MacBook"));
    }

    [Fact]
    public void Empty_state_points_at_the_star()
    {
        Assert.Contains("star", AutoConnectCopy.NoPreferredDescription);
        Assert.Contains("preferred", AutoConnectCopy.NoPreferredDescription);
    }

    [Fact]
    public void Prefer_tooltips_distinguish_pick_from_already_preferred()
    {
        Assert.Contains("auto-connect", AutoConnectCopy.PreferToolTip);
        Assert.Contains("Preferred", AutoConnectCopy.PreferredToolTip);
        Assert.Equal("Preferred", AutoConnectCopy.PreferredBadge);
    }

    [Fact]
    public void A_Config_failure_reads_as_password_rejected_not_an_endless_retry()
    {
        var exception = new AirPlayPasswordRejectedException("rejected");

        Assert.Equal(
            AutoConnectCopy.PasswordRejectedStatus,
            AutoConnectCopy.RetryingStatus(AutoConnectFailureKind.Config, exception));
    }

    [Fact]
    public void A_SETUP_400_reads_as_waiting_on_the_Mac_not_receiver_not_found()
    {
        var exception = new RtspStatusException(
            "SETUP session",
            400,
            "SETUP session failed with RTSP 400 Bad Request.");

        Assert.Equal(
            AutoConnectCopy.WaitingOnMacRetryingStatus,
            AutoConnectCopy.RetryingStatus(AutoConnectFailureKind.Silent, exception));
    }

    [Fact]
    public void An_absent_receiver_reads_as_not_found_distinct_from_the_SETUP_400_case()
    {
        var exception = new TimeoutException("dial timed out");

        Assert.Equal(
            AutoConnectCopy.ReceiverNotFoundRetryingStatus,
            AutoConnectCopy.RetryingStatus(AutoConnectFailureKind.Silent, exception));
    }

    [Fact]
    public void Both_Silent_sub_case_statuses_are_worded_as_retrying()
    {
        Assert.Contains("retrying", AutoConnectCopy.ReceiverNotFoundRetryingStatus);
        Assert.Contains("retrying", AutoConnectCopy.WaitingOnMacRetryingStatus);
    }
}
