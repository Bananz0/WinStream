using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinStream.Core.Audio;
using WinStream.Core.Network;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tools.LinkRxPi;

/// <summary>
/// Raspberry Pi / Linux companion: WSL1 UDP → ALSA via <c>aplay</c> (hw/plughw).
/// Avoids Pulse/PipeWire on the hot path when using <c>-D hw:0</c> or <c>plughw:0</c>.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var port = Wsl1Constants.DefaultMediaPort;
        string? pin = null;
        var device = "plughw:0,0";
        var ethernet = true;
        var advertise = true;
        var name = Environment.MachineName;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
                i++;
            }
            else if (args[i] == "--pin" && i + 1 < args.Length)
            {
                pin = args[++i];
            }
            else if (args[i] == "--device" && i + 1 < args.Length)
            {
                device = args[++i];
            }
            else if (args[i] == "--wifi")
            {
                ethernet = false;
            }
            else if (args[i] == "--name" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (args[i] == "--no-advertise")
            {
                advertise = false;
            }
        }

        if (string.IsNullOrWhiteSpace(pin))
        {
            Console.Error.WriteLine(
                "Usage: LinkRx.Pi --pin <PIN> [--port 47200] [--device plughw:0,0] [--wifi] " +
                "[--name <label>] [--no-advertise]");
            return 2;
        }

        var controlPort = port + LinkControlProtocol.DefaultControlPortOffset;
        Console.WriteLine($"LinkRx.Pi UDP:{port} TCP:{controlPort} pin={pin} alsa={device}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var aplay = StartAplay(device);
        await using var stdin = aplay.StandardInput.BaseStream;

        var control = new LinkPlayoutControlHandler(
            onStart: (mediaPort, format) => Console.WriteLine(
                $"START port={mediaPort} {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit"),
            onStop: () => Console.WriteLine("STOP"));

        await using var advertiser = advertise ? StartAdvertising(name, port) : null;

        var controlTask = RunControlAsync(controlPort, pin, control, cts.Token);
        var mediaTask = RunMediaAsync(port, stdin, ethernet, control, cts.Token);
        await Task.WhenAny(controlTask, mediaTask).ConfigureAwait(false);
        cts.Cancel();
        try
        {
            aplay.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort.
        }

        return 0;
    }

    /// <summary>Discovery is a convenience: a failure here must not block manual-IP use.</summary>
    private static LinkServiceAdvertiser? StartAdvertising(string name, int port)
    {
        try
        {
            return LinkServiceAdvertiser.Start(name, port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"mDNS advertising unavailable ({ex.Message}); connect by IP.");
            return null;
        }
    }

    private static Process StartAplay(string device)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "aplay",
            Arguments =
                $"-D {device} -f S16_LE -c {Wsl1Constants.DefaultChannels} -r {Wsl1Constants.DefaultSampleRate} -t raw -q -",
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start aplay. Install alsa-utils.");
        return process;
    }

    private static async Task RunControlAsync(
        int controlPort,
        string pin,
        LinkPlayoutControlHandler handler,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, controlPort);
        listener.Start();
        try
        {
            await LinkControlServer.ServeAsync(listener, pin, handler, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// aplay's pipe hides sink starvation, so the shared playout gate runs on sequence
    /// gaps alone here — the prime/re-prime behaviour still matches the Windows RX.
    /// </summary>
    private static async Task RunMediaAsync(
        int port,
        Stream alsa,
        bool ethernet,
        LinkPlayoutControlHandler control,
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(port);
        var format = new AudioFormat(
            Wsl1Constants.DefaultSampleRate,
            Wsl1Constants.DefaultChannels,
            16);
        var playout = new LinkPlayoutBuffer(format, ethernet, DateTimeOffset.UtcNow);
        control.Buffer = playout;
        var announcedTarget = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!Wsl1Packet.TryRead(result.Buffer, out var header, out var payload))
            {
                continue;
            }

            var push = playout.Push(header.Sequence, payload, DateTimeOffset.UtcNow);
            if (push.PausedForReprime)
            {
                Console.WriteLine($"Re-priming to ~{playout.TargetMilliseconds} ms");
            }

            while (playout.TryDrain(out var chunk))
            {
                await alsa.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            if (push.StartedPlayout && playout.TargetMilliseconds != announcedTarget)
            {
                announcedTarget = playout.TargetMilliseconds;
                Console.WriteLine($"Playout buffered to ~{announcedTarget} ms");
            }

            if (playout.PacketsAccepted % 500 == 0)
            {
                Console.WriteLine(
                    $"rx packets={playout.PacketsAccepted} jitterMs={playout.TargetMilliseconds} " +
                    $"late={playout.LateOrLostPackets} droppedBytes={playout.DroppedBytes}");
            }
        }
    }
}
