using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkTargetTests
{
    [Fact]
    public void Bare_host_uses_default_media_port_and_derived_control_port()
    {
        Assert.True(LinkTarget.TryParse(" 192.168.1.50 ", out var target));

        Assert.Equal("192.168.1.50", target.Host);
        Assert.Equal(Wsl1Constants.DefaultMediaPort, target.MediaPort);
        Assert.Equal(
            Wsl1Constants.DefaultMediaPort + LinkControlProtocol.DefaultControlPortOffset,
            target.ControlPort);
        Assert.Equal($"192.168.1.50:{Wsl1Constants.DefaultMediaPort}", target.Key);
    }

    [Fact]
    public void Explicit_port_moves_control_port_with_it()
    {
        Assert.True(LinkTarget.TryParse("linkrx.local:50000", out var target));

        Assert.Equal("linkrx.local", target.Host);
        Assert.Equal(50000, target.MediaPort);
        Assert.Equal(50001, target.ControlPort);
    }

    [Fact]
    public void Bracketed_ipv6_keeps_the_address_and_optional_port()
    {
        Assert.True(LinkTarget.TryParse("[fe80::1]:47300", out var withPort));
        Assert.Equal("fe80::1", withPort.Host);
        Assert.Equal(47300, withPort.MediaPort);

        Assert.True(LinkTarget.TryParse("[fe80::1]", out var withoutPort));
        Assert.Equal("fe80::1", withoutPort.Host);
        Assert.Equal(Wsl1Constants.DefaultMediaPort, withoutPort.MediaPort);
    }

    [Fact]
    public void Bare_ipv6_literal_is_not_split_on_its_colons()
    {
        Assert.True(LinkTarget.TryParse("fe80::1", out var target));

        Assert.Equal("fe80::1", target.Host);
        Assert.Equal(Wsl1Constants.DefaultMediaPort, target.MediaPort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":47200")]
    [InlineData("host:")]
    [InlineData("host:abc")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("[]:47200")]
    public void Unusable_input_is_rejected(string? text)
    {
        Assert.False(LinkTarget.TryParse(text, out var target));
        Assert.Null(target);
    }

    [Fact]
    public void Media_port_at_the_top_of_the_range_is_rejected_so_control_port_fits()
    {
        Assert.False(LinkTarget.TryParse("host:65535", out _));
    }
}
