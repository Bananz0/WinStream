using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tools.RaopProbe;

/// <summary>
/// Writes the exact payloads the AirPlay 2 handshake sends so they can be validated
/// against a reference plist parser. A receiver answering 400 usually means the
/// bytes below are not a well-formed bplist00.
/// </summary>
public static class PlistSelfTest
{
    public static int Run()
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "plist-selftest");
        Directory.CreateDirectory(outputDirectory);

        var sharedPeer = new Dictionary<string, object>
        {
            ["Addresses"] = new List<object> { "192.168.1.100" },
            ["ID"] = "AA:BB:CC:DD:EE:FF",
            ["SupportsClockPortMatchingOverride"] = false
        };

        var payloads = new Dictionary<string, Dictionary<string, object>>
        {
            // Same instance referenced twice: verifies the writer emits a legal
            // shared object rather than a broken back-reference.
            ["shared-reference"] = new()
            {
                ["timingPeerInfo"] = sharedPeer,
                ["timingPeerList"] = new List<object> { sharedPeer }
            },
            ["info"] = new()
            {
                ["qualifier"] = new List<object> { "txtAirPlay" }
            },
            ["session-setup"] = new()
            {
                ["deviceID"] = "AA:BB:CC:DD:EE:FF",
                ["macAddress"] = "AA:BB:CC:DD:EE:FF",
                ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["timingProtocol"] = "NTP",
                ["timingPort"] = 0L,
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
            },
            ["stream-setup"] = new()
            {
                ["streams"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = 96L,
                        ["audioFormat"] = 0x40000L,
                        ["audioMode"] = "default",
                        ["ct"] = 2L,
                        ["isMedia"] = true,
                        ["latencyMin"] = 11025L,
                        ["latencyMax"] = 88200L,
                        ["spf"] = 352L,
                        ["sr"] = 44100L,
                        ["controlPort"] = 6001L,
                        ["shk"] = new byte[32],
                        ["supportsDynamicStreamID"] = true,
                        ["streamConnectionID"] = 1234567L
                    }
                }
            }
        };

        foreach (var (name, payload) in payloads)
        {
            var bytes = BinaryPlist.Write(payload);
            var path = Path.Combine(outputDirectory, $"{name}.plist");
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"PLIST {name} bytes={bytes.Length} path={path}");

            try
            {
                var roundTripped = BinaryPlist.Read(bytes);
                var keys = roundTripped is IDictionary<string, object> map
                    ? string.Join(",", map.Keys)
                    : roundTripped?.GetType().Name ?? "null";
                Console.WriteLine($"  self-read {keys}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SELF_READ_FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"OUTPUT_DIR={outputDirectory}");
        return 0;
    }
}
