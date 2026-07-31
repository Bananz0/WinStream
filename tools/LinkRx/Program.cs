using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using WinStream.Core.Audio;
using WinStream.Core.Network;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tools.LinkRx;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var port = Wsl1Constants.DefaultMediaPort;
        var ethernet = true;
        var advertise = true;
        string? pin = null;
        var name = Environment.MachineName;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var parsed))
            {
                port = parsed;
                i++;
            }
            else if (args[i] == "--wifi")
            {
                ethernet = false;
            }
            else if (args[i] == "--pin" && i + 1 < args.Length)
            {
                pin = args[++i];
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
                "Usage: LinkRx --pin <PIN> [--port 47200] [--wifi] [--name <label>] [--no-advertise]");
            return 2;
        }

        var controlPort = port + LinkControlProtocol.DefaultControlPortOffset;
        Console.WriteLine(
            $"LinkRx UDP :{port} TCP control :{controlPort} pin={pin} " +
            $"jitterStart={(ethernet ? LinkJitterController.EthernetStartMs : LinkJitterController.OtherStartMs)}ms");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var control = new LinkPlayoutControlHandler(
            onStart: (mediaPort, format) => Console.WriteLine(
                $"START port={mediaPort} {format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit"),
            onStop: () => Console.WriteLine("STOP"));

        await using var advertiser = advertise ? StartAdvertising(name, port) : null;

        try
        {
            var controlTask = RunControlAsync(controlPort, pin, control, cts.Token);
            var mediaTask = RunAsync(port, ethernet, control, cts.Token);
            await Task.WhenAny(controlTask, mediaTask).ConfigureAwait(false);
            cts.Cancel();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
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

    private static async Task RunAsync(
        int port,
        bool ethernet,
        LinkPlayoutControlHandler control,
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(port);
        var format = new AudioFormat(
            Wsl1Constants.DefaultSampleRate,
            Wsl1Constants.DefaultChannels,
            16);
        var waveFormat = new WaveFormat(format.SampleRate, 16, format.Channels);
        using var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, latency: 10);
        var provider = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(LinkPlayoutBuffer.DefaultCapacityMilliseconds),
            DiscardOnBufferOverflow = true
        };
        output.Init(provider);

        var playout = new LinkPlayoutBuffer(format, ethernet, DateTimeOffset.UtcNow);
        control.Buffer = playout;
        var rendering = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!Wsl1Packet.TryRead(result.Buffer, out var header, out var payload))
            {
                continue;
            }

            var sinkStarved = rendering && provider.BufferedBytes == 0;
            var push = playout.Push(header.Sequence, payload, DateTimeOffset.UtcNow, sinkStarved);
            if (push.PausedForReprime && rendering)
            {
                output.Pause();
                rendering = false;
                Console.WriteLine($"Re-priming to ~{playout.TargetMilliseconds} ms");
            }

            while (playout.TryDrain(out var chunk))
            {
                provider.AddSamples(chunk, 0, chunk.Length);
            }

            if (playout.IsPlaying && !rendering)
            {
                output.Play();
                rendering = true;
                Console.WriteLine($"Playout buffered to ~{playout.TargetMilliseconds} ms");
            }

            if (playout.PacketsAccepted % 500 == 0)
            {
                Console.WriteLine(
                    $"rx packets={playout.PacketsAccepted} jitterMs={playout.TargetMilliseconds} " +
                    $"underruns={playout.Underruns} late={playout.LateOrLostPackets} " +
                    $"droppedBytes={playout.DroppedBytes}");
            }
        }
    }
}
