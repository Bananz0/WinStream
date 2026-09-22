using WinStream.Core.Network;

namespace WinStream.Tests;

public class RaopInstanceNameTests
{
    [Fact]
    public void Reads_the_device_id_from_the_mac_prefix()
    {
        Assert.Equal(
            "AA:BB:CC:DD:EE:FF",
            RaopInstanceName.DeviceIdOrEmpty("AABBCCDDEEFF@living-room"));
    }

    [Fact]
    public void Lowercase_hex_is_normalized()
    {
        Assert.Equal(
            "42:AE:E9:D5:A5:60",
            RaopInstanceName.DeviceIdOrEmpty("42aee9d5a560@office-speaker"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("living-room")]
    [InlineData("living-room@AABBCCDDEEFF")]
    [InlineData("AABBCCDDEE@short")]
    [InlineData("AABBCCDDEEFFE@toolong")]
    [InlineData("AA:BB:CC:DD:EE:FF@colons")]
    [InlineData("ZZZZZZZZZZZZ@nothex")]
    public void Names_without_a_mac_prefix_yield_no_identity(string instanceName)
    {
        Assert.Equal(string.Empty, RaopInstanceName.DeviceIdOrEmpty(instanceName));
    }

    [Fact]
    public void A_null_name_yields_no_identity()
    {
        Assert.Equal(string.Empty, RaopInstanceName.DeviceIdOrEmpty(null));
    }

    [Fact]
    public void NameAfterAt_returns_the_part_after_the_mac_prefix()
    {
        Assert.Equal(
            "living-room",
            RaopInstanceName.NameAfterAtOrSelf("AABBCCDDEEFF@living-room"));
    }

    [Fact]
    public void NameAfterAt_returns_the_whole_name_when_there_is_no_at()
    {
        Assert.Equal("living-room", RaopInstanceName.NameAfterAtOrSelf("living-room"));
    }

    [Fact]
    public void NameAfterAt_of_a_null_or_empty_name_is_empty()
    {
        Assert.Equal(string.Empty, RaopInstanceName.NameAfterAtOrSelf(null));
        Assert.Equal(string.Empty, RaopInstanceName.NameAfterAtOrSelf(string.Empty));
    }
}
