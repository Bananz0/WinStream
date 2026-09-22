using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tools.LinkTx;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var host = args[0];
        var port = Wsl1Constants.DefaultMediaPort;
        var seconds = 30;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
                i++;
            }
            else if (args[i] == "--seconds" && i + 1 < args.Length &&
                     int.TryParse(args[i + 1], out var s))
            {
                seconds = s;
                i++;
            }
        }

        var format = new AudioFormat(
            Wsl1Constants.DefaultSampleRate,
            Wsl1Constants.DefaultChannels,
            16);
        await using var session = new LinkSession();
        await session.ConnectAsync(host, port);

        Console.WriteLine($"LinkTx → {host}:{port} for {seconds}s (440 Hz tone)");
        var frameBytes = Wsl1Constants.DefaultPayloadBytes;
        var pcm = new byte[frameBytes];
        FillSine(pcm, format, phase: 0);
        var phase = Wsl1Constants.DefaultSamplesPerChannel;
        var end = DateTimeOffset.UtcNow.AddSeconds(seconds);

        while (DateTimeOffset.UtcNow < end)
        {
            FillSine(pcm, format, phase);
            phase += Wsl1Constants.DefaultSamplesPerChannel;
            session.SubmitPcm(pcm, format, System.Diagnostics.Stopwatch.GetTimestamp());
            await Task.Delay(Wsl1Constants.DefaultSamplesPerChannel * 1000 / format.SampleRate)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"Sent {session.PacketsSent} packets");
        await session.DisconnectAsync().ConfigureAwait(false);
        return 0;
    }

    private static void FillSine(Span<byte> pcm, AudioFormat format, int phase)
    {
        const double frequencyHz = 440.0;
        var sampleCount = pcm.Length / format.BlockAlign;
        for (var i = 0; i < sampleCount; i++)
        {
            var t = (phase + i) / (double)format.SampleRate;
            var sample = (short)(Math.Sin(t * Math.PI * 2 * frequencyHz) * short.MaxValue * 0.25);
            var offset = i * format.BlockAlign;
            BitConverter.TryWriteBytes(pcm.Slice(offset, 2), sample);
            BitConverter.TryWriteBytes(pcm.Slice(offset + 2, 2), sample);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: LinkTx <host> [--port 47200] [--seconds 30] [--tone]");
        Console.WriteLine("Default: 440 Hz tone via LinkSession (no WASAPI).");
        Console.WriteLine("Lab capture path lives in WinStream.LinkOrchestrator (Phase 2).");
    }
}
