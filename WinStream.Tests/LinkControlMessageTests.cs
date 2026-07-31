using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;

namespace WinStream.Tests;

public class LinkControlMessageTests
{
    [Theory]
    [InlineData("HELLO", LinkControlVerb.Hello)]
    [InlineData("PIN", LinkControlVerb.Pin)]
    [InlineData("START", LinkControlVerb.Start)]
    [InlineData("STOP", LinkControlVerb.Stop)]
    [InlineData("STAT", LinkControlVerb.Stat)]
    [InlineData("BYE", LinkControlVerb.Bye)]
    [InlineData("OK", LinkControlVerb.Ok)]
    [InlineData("FAIL", LinkControlVerb.Fail)]
    public void Parse_maps_every_known_keyword(string line, LinkControlVerb expected)
    {
        Assert.Equal(expected, LinkControlMessage.Parse(line).Verb);
    }

    [Fact]
    public void Parse_splits_the_argument_from_the_verb()
    {
        var message = LinkControlMessage.Parse("PIN 1234");

        Assert.Equal(LinkControlVerb.Pin, message.Verb);
        Assert.Equal("1234", message.Argument);
    }

    [Fact]
    public void Parse_keeps_inner_spaces_in_the_argument()
    {
        var message = LinkControlMessage.Parse("FAIL pin rejected by receiver");

        Assert.Equal("pin rejected by receiver", message.Argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_treats_blank_lines_as_unknown(string line)
    {
        var message = LinkControlMessage.Parse(line);

        Assert.Equal(LinkControlVerb.Unknown, message.Verb);
        Assert.Equal(string.Empty, message.Argument);
    }

    [Fact]
    public void Parse_is_case_sensitive_so_lowercase_is_not_a_verb()
    {
        // The wire format is uppercase ASCII; accepting "start" would let a sloppy
        // receiver drift from the spec.
        Assert.Equal(LinkControlVerb.Unknown, LinkControlMessage.Parse("start 1 2 3 4").Verb);
    }

    [Fact]
    public void Parse_preserves_the_whole_line_for_an_unknown_verb()
    {
        var message = LinkControlMessage.Parse("WAT now");

        Assert.Equal(LinkControlVerb.Unknown, message.Verb);
        Assert.Equal("WAT now", message.Argument);
    }

    [Fact]
    public void Start_round_trips_through_Parse()
    {
        var format = new AudioFormat(48000, 2, 16);

        var parsed = LinkControlMessage.Parse(LinkControlMessage.Start(47200, format).ToString());

        Assert.True(parsed.TryReadStart(out var port, out var readBack));
        Assert.Equal(47200, port);
        Assert.Equal(48000, readBack.SampleRate);
        Assert.Equal(2, readBack.Channels);
        Assert.Equal(16, readBack.BitsPerSample);
    }

    [Fact]
    public void Start_rejects_a_null_format()
    {
        Assert.Throws<ArgumentNullException>(() => LinkControlMessage.Start(47200, null!));
    }

    [Theory]
    [InlineData("47200 48000 2")]
    [InlineData("47200 48000 2 16 99")]
    [InlineData("47200 48000 2 abc")]
    [InlineData("0 48000 2 16")]
    [InlineData("47200 -48000 2 16")]
    public void TryReadStart_rejects_malformed_arguments(string argument)
    {
        var message = LinkControlMessage.Parse($"START {argument}");

        Assert.False(message.TryReadStart(out var port, out _));
        Assert.Equal(0, port);
    }

    [Fact]
    public void TryReadStart_rejects_a_non_start_message()
    {
        Assert.False(LinkControlMessage.Ok.TryReadStart(out _, out _));
    }

    [Fact]
    public void Telemetry_round_trips_through_Parse()
    {
        var sent = new LinkReceiverTelemetry(3, 4, 5, 6000);

        var parsed = LinkControlMessage.Parse(LinkControlMessage.Telemetry(sent).ToString());

        Assert.True(parsed.TryReadTelemetry(out var readBack));
        Assert.Equal(sent, readBack);
    }

    [Theory]
    [InlineData("underruns")]
    [InlineData("underruns=1 late=2 jitterMs=3")]
    [InlineData("underruns=1 late=2 jitterMs=3 packets=4 extra=5")]
    [InlineData("underruns=1 late=2 jitterMs=3 packets=-4")]
    [InlineData("underruns=1 late=2 jitterMs=3 packets=x")]
    [InlineData("=1 late=2 jitterMs=3 packets=4")]
    [InlineData("underruns late=2 jitterMs=3 packets=4")]
    public void TryReadTelemetry_rejects_malformed_counters(string argument)
    {
        var message = LinkControlMessage.Parse($"STAT {argument}");

        Assert.False(message.TryReadTelemetry(out _));
    }

    [Fact]
    public void TryReadTelemetry_rejects_a_bare_stat_request()
    {
        // STAT with no argument is the poll, not a reply.
        Assert.False(LinkControlMessage.Stat.TryReadTelemetry(out _));
    }

    [Fact]
    public void TryReadTelemetry_clamps_a_jitter_value_past_int_range()
    {
        var message = LinkControlMessage.Parse(
            $"STAT underruns=0 late=0 jitterMs={(long)int.MaxValue + 10} packets=1");

        Assert.True(message.TryReadTelemetry(out var telemetry));
        Assert.Equal(int.MaxValue, telemetry.JitterMilliseconds);
    }

    [Fact]
    public void ToString_omits_the_separator_when_there_is_no_argument()
    {
        Assert.Equal("HELLO", LinkControlMessage.Hello.ToString());
        Assert.Equal("PIN 42", LinkControlMessage.Pin("42").ToString());
        Assert.Equal("FAIL nope", LinkControlMessage.Fail("nope").ToString());
    }
}
