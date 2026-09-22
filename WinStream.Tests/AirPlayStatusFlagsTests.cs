using WinStream.Core.Network;

namespace WinStream.Tests;

public class AirPlayStatusFlagsTests
{
    [Theory]
    [InlineData("0x284", true)]
    [InlineData("0x204", false)]
    [InlineData("644", true)]
    [InlineData("516", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PasswordRequired_matches_the_documented_bit(string? raw, bool expected)
    {
        var flags = AirPlayStatusFlags.Parse(raw);
        Assert.Equal(expected, AirPlayStatusFlags.RequiresPassword(flags));
    }

    [Fact]
    public void DeviceInfo_exposes_the_flag()
    {
        var device = new DeviceInfo { StatusFlags = 0x284 };
        Assert.True(device.RequiresPassword);

        device.StatusFlags = 0x204;
        Assert.False(device.RequiresPassword);
    }
}
