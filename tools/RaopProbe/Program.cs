using System.Diagnostics;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Streaming;
using WinStream.Network;
using WinStream.Streaming;

// Headless RAOP diagnostic harness. Drives the same RaopSession the app uses so
// protocol fixes can be validated without the WinUI shell.

var nameFilter = args.FirstOrDefault(a => !a.StartsWith("--"));
var listOnly = args.Contains("--list");
var seconds = ParseInt(args, "--seconds", 15);

AppLog.LineWritten += (_, line) => Console.WriteLine($"  log| {line}");

Console.WriteLine("== Discovering _raop._tcp receivers ==");
using var discoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
var devices = await DeviceDiscovery.DiscoverDevicesAsync(discoveryCts.Token);

if (devices.Count == 0)
{
    Console.WriteLine("No receivers found.");
    return 2;
}

foreach (var d in devices)
{
    var classic = AirPlayCapability.SupportsClassicRaop(d.EncryptionTypes);
    var ap2 = AirPlayCapability.SupportsAirPlay2(
        !string.IsNullOrWhiteSpace(d.PublicCUAirPlayPairingIdentity),
        d.Features,
        d.AirPlayVersion);
    Console.WriteLine(
        $"- {d.DisplayName}  {d.IPAddress}:{d.Port}\n" +
        $"    model={d.Model} srcvers={d.AirPlayVersion} protovers={d.ProtocolVersion}\n" +
        $"    et='{d.EncryptionTypes}' features='{d.FeaturesRaw}' pkLen={d.PublicKey?.Length ?? 0}\n" +
        $"    classicRaop={classic} airPlay2={ap2}");
}

if (listOnly)
{
    return 0;
}

var target = nameFilter is null
    ? devices[0]
    : devices.FirstOrDefault(d =>
        (d.DisplayName ?? string.Empty).Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

if (target is null)
{
    Console.WriteLine($"No receiver matching '{nameFilter}'.");
    return 2;
}

if (args.Contains("--watch"))
{
    var ready = false;
    foreach (var device in devices)
    {
        var status = await WinStream.Tools.RaopProbe.RawProbe.GetOptionsStatusAsync(
            device.IPAddress,
            device.Port);
        Console.WriteLine(
            $"AIRPLAY_STATUS name={device.DisplayName} host={device.IPAddress}:{device.Port} " +
            $"et='{device.EncryptionTypes}' options={status}");
        if (status == 200)
        {
            Console.WriteLine(
                $"AIRPLAY_READY name={device.DisplayName} host={device.IPAddress}:{device.Port}");
            ready = true;
        }
    }

    return ready ? 0 : 1;
}

if (args.Contains("--check"))
{
    var status = await WinStream.Tools.RaopProbe.RawProbe.GetOptionsStatusAsync(
        target.IPAddress,
        target.Port);
    Console.WriteLine($"AIRPLAY_OPTIONS_STATUS={status} host={target.IPAddress}");
    return status == 200 ? 0 : 1;
}

if (args.Contains("--raw"))
{
    await WinStream.Tools.RaopProbe.RawProbe.RunAsync(target.IPAddress, target.Port);
    return 0;
}

Console.WriteLine($"\n== Connecting to {target.DisplayName} ({target.IPAddress}:{target.Port}) ==");

await using var session = new RaopSession(target);
session.StateChanged += (_, change) =>
    Console.WriteLine($"  state| {change.Current}{(change.Reason is null ? "" : $" — {change.Reason}")}");

try
{
    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await session.ConnectAsync(connectCts.Token);
}
catch (Exception ex)
{
    Console.WriteLine($"\nCONNECT FAILED: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.WriteLine($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    return 1;
}

Console.WriteLine($"\n== Streaming {seconds}s of 440 Hz test tone ==");
var format = new AudioFormat(44100, 2, 16);
var stopwatch = Stopwatch.StartNew();
var phase = 0.0;
const double toneHz = 440.0;
const int chunkFrames = 441; // 10 ms

while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
{
    var pcm = new byte[chunkFrames * format.BlockAlign];
    for (var i = 0; i < chunkFrames; i++)
    {
        phase += 2 * Math.PI * toneHz / format.SampleRate;
        var sample = (short)(Math.Sin(phase) * 8000);
        var offset = i * format.BlockAlign;
        BitConverter.TryWriteBytes(pcm.AsSpan(offset), sample);
        BitConverter.TryWriteBytes(pcm.AsSpan(offset + 2), sample);
    }

    session.SubmitPcm(pcm, format);
    await Task.Delay(10);

    if (session.State is SessionState.Failed or SessionState.Disconnected)
    {
        Console.WriteLine($"\nSTREAM DROPPED: state={session.State}");
        return 1;
    }
}

Console.WriteLine($"\nStreamed {seconds}s. Final state: {session.State}");
await session.DisconnectAsync();
Console.WriteLine("Disconnected cleanly.");
return session.State == SessionState.Disconnected ? 0 : 1;

static int ParseInt(string[] args, string flag, int fallback)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
        ? value
        : fallback;
}
