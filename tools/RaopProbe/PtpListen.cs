using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WinStream.Tools.RaopProbe;

/// <summary>
/// Binds the IEEE-1588 event/general ports while another probe runs so we can see
/// whether the receiver is actually trying to reach a PTP clock on this host.
/// </summary>
public static class PtpListen
{
    private const int EventPort = 319;
    private const int GeneralPort = 320;

    public static async Task<int> RunAsync(Func<Task<int>> body)
    {
        var hits = new ConcurrentBag<string>();
        var elapsed = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();

        var listeners = new List<Task>();
        foreach (var port in new[] { EventPort, GeneralPort })
        {
            if (TryBind(port, out var socket))
            {
                Console.WriteLine($"  ptp| listening on udp/{port}");
                listeners.Add(ListenAsync(socket!, port, hits, elapsed, cts.Token));
            }
            else
            {
                Console.WriteLine($"  ptp| FAILED to bind udp/{port} (in use?)");
            }
        }

        int exitCode;
        try
        {
            exitCode = await body();
        }
        finally
        {
            cts.Cancel();
            foreach (var listener in listeners)
            {
                try
                {
                    await listener;
                }
                catch (OperationCanceledException)
                {
                    // Expected once the listeners are torn down.
                }
            }
        }

        Console.WriteLine($"\n== PTP inbound summary: {hits.Count} packet(s) ==");
        foreach (var line in hits.Take(40))
        {
            Console.WriteLine($"  {line}");
        }

        if (hits.IsEmpty)
        {
            Console.WriteLine("  PTP_SILENT — receiver never contacted a clock on this host.");
        }

        return exitCode;
    }

    private static bool TryBind(int port, out UdpClient? socket)
    {
        try
        {
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            socket = null;
            return false;
        }
    }

    private static async Task ListenAsync(
        UdpClient socket,
        int port,
        ConcurrentBag<string> hits,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        using (socket)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                var buffer = result.Buffer;
                var type = buffer.Length > 0 ? MessageName(buffer[0] & 0x0f) : "empty";
                hits.Add(
                    $"{elapsed.Elapsed.TotalSeconds,6:F2}s udp/{port} from {result.RemoteEndPoint} " +
                    $"{buffer.Length}B type={type}");
            }
        }
    }

    private static string MessageName(int messageType) => messageType switch
    {
        0x0 => "Sync",
        0x1 => "Delay_Req",
        0x2 => "Pdelay_Req",
        0x3 => "Pdelay_Resp",
        0x8 => "Follow_Up",
        0x9 => "Delay_Resp",
        0xa => "Pdelay_Resp_Follow_Up",
        0xb => "Announce",
        0xc => "Signaling",
        0xd => "Management",
        _ => $"0x{messageType:x}"
    };
}
