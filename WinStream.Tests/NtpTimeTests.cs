using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class NtpTimeTests
{
    [Fact]
    public void Now_is_after_ntp_epoch()
    {
        var ntp = NtpTime.Now();
        var seconds = ntp >> 32;

        // NTP epoch is 1900; Unix epoch offset is 2208988800.
        Assert.True(seconds > 2208988800UL);
        Assert.True(seconds < 2208988800UL + 200UL * 365 * 24 * 3600);
    }
}
