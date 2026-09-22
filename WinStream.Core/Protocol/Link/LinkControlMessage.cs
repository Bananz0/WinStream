using System.Globalization;
using WinStream.Core.Audio;

namespace WinStream.Core.Protocol.Link;

public enum LinkControlVerb
{
    Unknown,
    Hello,
    Pin,
    Start,
    Stop,
    Stat,
    Bye,
    Ok,
    Fail
}

/// <summary>
/// One line of the Link control plane. Newline-framed ASCII keeps the channel
/// greppable in a capture and cheap to implement on a Pi.
/// </summary>
public readonly record struct LinkControlMessage(LinkControlVerb Verb, string Argument)
{
    public static readonly LinkControlMessage Hello = new(LinkControlVerb.Hello, string.Empty);
    public static readonly LinkControlMessage Stop = new(LinkControlVerb.Stop, string.Empty);
    public static readonly LinkControlMessage Stat = new(LinkControlVerb.Stat, string.Empty);
    public static readonly LinkControlMessage Bye = new(LinkControlVerb.Bye, string.Empty);
    public static readonly LinkControlMessage Ok = new(LinkControlVerb.Ok, string.Empty);

    public static LinkControlMessage Pin(string pin) => new(LinkControlVerb.Pin, pin);

    public static LinkControlMessage Fail(string reason) => new(LinkControlVerb.Fail, reason);

    public static LinkControlMessage Start(int mediaPort, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return new LinkControlMessage(
            LinkControlVerb.Start,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{mediaPort} {format.SampleRate} {format.Channels} {format.BitsPerSample}"));
    }

    public static LinkControlMessage Telemetry(LinkReceiverTelemetry telemetry) =>
        new(
            LinkControlVerb.Stat,
            string.Create(
                CultureInfo.InvariantCulture,
                $"underruns={telemetry.Underruns} late={telemetry.LateOrLostPackets} " +
                $"jitterMs={telemetry.JitterMilliseconds} packets={telemetry.PacketsReceived}"));

    public static LinkControlMessage Parse(string line)
    {
        var trimmed = line?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return new LinkControlMessage(LinkControlVerb.Unknown, string.Empty);
        }

        var space = trimmed.IndexOf(' ');
        var head = space < 0 ? trimmed : trimmed[..space];
        var argument = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        var verb = head switch
        {
            "HELLO" => LinkControlVerb.Hello,
            "PIN" => LinkControlVerb.Pin,
            "START" => LinkControlVerb.Start,
            "STOP" => LinkControlVerb.Stop,
            "STAT" => LinkControlVerb.Stat,
            "BYE" => LinkControlVerb.Bye,
            "OK" => LinkControlVerb.Ok,
            "FAIL" => LinkControlVerb.Fail,
            _ => LinkControlVerb.Unknown
        };

        return verb == LinkControlVerb.Unknown
            ? new LinkControlMessage(LinkControlVerb.Unknown, trimmed)
            : new LinkControlMessage(verb, argument);
    }

    /// <summary>Reads the <c>START</c> arguments; false when any field is malformed.</summary>
    public bool TryReadStart(out int mediaPort, out AudioFormat format)
    {
        mediaPort = 0;
        format = null!;
        if (Verb != LinkControlVerb.Start)
        {
            return false;
        }

        var fields = Argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 4 ||
            !TryPositive(fields[0], out mediaPort) ||
            !TryPositive(fields[1], out var sampleRate) ||
            !TryPositive(fields[2], out var channels) ||
            !TryPositive(fields[3], out var bitsPerSample))
        {
            mediaPort = 0;
            return false;
        }

        format = new AudioFormat(sampleRate, channels, bitsPerSample);
        return true;
    }

    /// <summary>Reads a <c>STAT</c> reply; false when a counter is missing or malformed.</summary>
    public bool TryReadTelemetry(out LinkReceiverTelemetry telemetry)
    {
        telemetry = default;
        if (Verb != LinkControlVerb.Stat || Argument.Length == 0)
        {
            return false;
        }

        long underruns = 0, late = 0, packets = 0;
        var jitterMs = 0;
        var seen = 0;
        foreach (var field in Argument.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = field.IndexOf('=');
            if (split <= 0)
            {
                return false;
            }

            var key = field[..split];
            var value = field[(split + 1)..];
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 0)
            {
                return false;
            }

            switch (key)
            {
                case "underruns":
                    underruns = parsed;
                    seen++;
                    break;
                case "late":
                    late = parsed;
                    seen++;
                    break;
                case "jitterMs":
                    jitterMs = (int)Math.Min(parsed, int.MaxValue);
                    seen++;
                    break;
                case "packets":
                    packets = parsed;
                    seen++;
                    break;
                default:
                    return false;
            }
        }

        if (seen != 4)
        {
            return false;
        }

        telemetry = new LinkReceiverTelemetry(underruns, late, jitterMs, packets);
        return true;
    }

    public override string ToString() => Verb switch
    {
        LinkControlVerb.Unknown => Argument,
        _ => Argument.Length == 0 ? Keyword(Verb) : $"{Keyword(Verb)} {Argument}"
    };

    private static string Keyword(LinkControlVerb verb) => verb switch
    {
        LinkControlVerb.Hello => "HELLO",
        LinkControlVerb.Pin => "PIN",
        LinkControlVerb.Start => "START",
        LinkControlVerb.Stop => "STOP",
        LinkControlVerb.Stat => "STAT",
        LinkControlVerb.Bye => "BYE",
        LinkControlVerb.Ok => "OK",
        LinkControlVerb.Fail => "FAIL",
        _ => string.Empty
    };

    private static bool TryPositive(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
}

/// <summary>Receiver-reported health, used to gate the SLA badge and to log soaks.</summary>
public readonly record struct LinkReceiverTelemetry(
    long Underruns,
    long LateOrLostPackets,
    int JitterMilliseconds,
    long PacketsReceived);
