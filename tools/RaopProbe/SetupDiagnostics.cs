using System.Net;
using System.Net.Sockets;
using System.Text;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Network;

namespace WinStream.Tools.RaopProbe;

/// <summary>
/// Pairs, dumps the receiver's /info, then tries session SETUP variants so the
/// exact key the receiver rejects can be identified from its response.
/// </summary>
public static class SetupDiagnostics
{
    public static async Task<int> RunAsync(DeviceInfo target, CancellationToken cancellationToken = default)
    {
        var localIp = ResolveLocalAddress(target.IPAddress);
        Console.WriteLine($"local address toward receiver: {localIp}");

        foreach (var variant in BuildVariants(localIp))
        {
            Console.WriteLine($"\n== SETUP variant: {variant.Name} ==");
            try
            {
                await using var control = new EncryptedRtspClient(target.IPAddress, target.Port);
                await control.ConnectAndPairAsync(cancellationToken);
                Console.WriteLine("  paired");

                var info = await control.SendAsync(
                    "GET",
                    "/info",
                    new Dictionary<string, string>
                    {
                        ["X-Apple-ProtocolVersion"] = "1",
                        ["Content-Type"] = "application/x-apple-binary-plist"
                    },
                    BinaryPlist.Write(new Dictionary<string, object>
                    {
                        ["qualifier"] = new List<object> { "txtAirPlay" }
                    }),
                    cancellationToken);
                Console.WriteLine($"  /info -> {info.StatusCode} {info.ReasonPhrase} bodyLen={info.Body.Length}");
                DescribeInfo(info.Body);

                var body = BinaryPlist.Write(variant.Payload(control));
                var response = await control.SendAsync(
                    "SETUP",
                    $"rtsp://{target.IPAddress}/{control.SessionUuid}",
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/x-apple-binary-plist"
                    },
                    body,
                    cancellationToken);

                Console.WriteLine($"  SETUP -> {response.StatusCode} {response.ReasonPhrase}");
                foreach (var header in response.Headers)
                {
                    Console.WriteLine($"    {header.Key}: {header.Value}");
                }

                DescribeBody(response.Body);

                if (response.IsSuccessStatusCode)
                {
                    // The receiver refuses a new session while one is open, so a
                    // leaked session here would poison every later probe run.
                    await control.TeardownAsync(cancellationToken);
                    Console.WriteLine($"  SETUP_OK variant={variant.Name} (torn down)");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  VARIANT_FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("\nAll SETUP variants failed.");
        return 1;
    }

    private static IEnumerable<Variant> BuildVariants(string localIp)
    {
        // Product path is PTP-only on this Mac; keep one NTP negative control.
        yield return new Variant(
            "NTP negative control",
            control => Common(control, "NTP", BoundUdpPort(), localIp, includePeerInfo: false));

        yield return new Variant(
            "PTP with timingPeerInfo",
            control => Common(control, "PTP", timingPort: null, localIp, includePeerInfo: true));
    }

    /// <summary>Binds an ephemeral UDP port so the advertised timing port is real.</summary>
    private static long BoundUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static Dictionary<string, object> Common(
        EncryptedRtspClient control,
        string timingProtocol,
        long? timingPort,
        string localIp,
        bool includePeerInfo)
    {
        var payload = new Dictionary<string, object>
        {
            ["deviceID"] = control.DeviceId,
            ["macAddress"] = control.DeviceId,
            ["sessionUUID"] = control.SessionUuid,
            ["timingProtocol"] = timingProtocol,
            ["name"] = "WinStream",
            ["model"] = "WinStream",
            ["sourceVersion"] = "415.3",
            ["osName"] = "Windows",
            ["osVersion"] = "10.0",
            ["osBuildVersion"] = "19041",
            ["groupUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            ["groupContainsGroupLeader"] = false,
            ["isMultiSelectAirPlay"] = false,
            ["senderSupportsRelay"] = false
        };

        if (timingPort.HasValue)
        {
            payload["timingPort"] = timingPort.Value;
        }

        if (includePeerInfo)
        {
            payload["timingPeerInfo"] = new Dictionary<string, object>
            {
                ["Addresses"] = new List<object> { localIp },
                ["ID"] = control.DeviceId,
                ["SupportsClockPortMatchingOverride"] = false
            };
            payload["timingPeerList"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["Addresses"] = new List<object> { localIp },
                    ["ID"] = control.DeviceId,
                    ["SupportsClockPortMatchingOverride"] = false
                }
            };
        }

        return payload;
    }

    private static void DescribeInfo(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
        {
            return;
        }

        var dumpPath = Path.Combine(AppContext.BaseDirectory, "info-response.plist");
        File.WriteAllBytes(dumpPath, body.ToArray());
        Console.WriteLine($"    saved {dumpPath}");

        try
        {
            if (BinaryPlist.Read(body.Span) is IDictionary<string, object?> map)
            {
                foreach (var key in map.Keys.OrderBy(k => k))
                {
                    var value = map[key];
                    var text = value switch
                    {
                        byte[] data => $"<{data.Length} bytes>",
                        null => "null",
                        _ => value.ToString()
                    };
                    if (text is { Length: > 60 })
                    {
                        text = text[..60] + "…";
                    }

                    Console.WriteLine($"    info.{key} = {text}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    info parse failed: {ex.GetType().Name}");
        }
    }

    private static void DescribeBody(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
        {
            Console.WriteLine("    <empty body>");
            return;
        }

        var span = body.Span;
        if (span.Length >= 8 && span[..8].SequenceEqual("bplist00"u8))
        {
            try
            {
                Console.WriteLine($"    body plist: {Describe(BinaryPlist.Read(span))}");
                return;
            }
            catch
            {
                // fall through to text
            }
        }

        Console.WriteLine($"    body text: {Encoding.UTF8.GetString(span).Trim()}");
    }

    private static string Describe(object? value) => value switch
    {
        IDictionary<string, object?> map =>
            "{" + string.Join(", ", map.Select(p => $"{p.Key}={Describe(p.Value)}")) + "}",
        IEnumerable<object?> list => "[" + string.Join(", ", list.Select(Describe)) + "]",
        byte[] data => $"<{data.Length} bytes>",
        null => "null",
        _ => value.ToString() ?? string.Empty
    };

    private static string ResolveLocalAddress(string receiverIp)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse(receiverIp), 7000));
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    private sealed record Variant(string Name, Func<EncryptedRtspClient, Dictionary<string, object>> Payload);
}
