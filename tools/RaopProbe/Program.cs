using System.Diagnostics;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;
using WinStream.Core.Network;

// Headless RAOP diagnostic harness. Drives the same RaopSession the app uses so
// protocol fixes can be validated without the WinUI shell.

var nameFilter = args.FirstOrDefault(a => !a.StartsWith("--"));
var listOnly = args.Contains("--list");
var seconds = ParseInt(args, "--seconds", 15);

AppLog.LineWritten += (_, line) => Console.WriteLine($"  log| {line}");

if (args.Contains("--txt"))
{
    return await WinStream.Tools.RaopProbe.TxtDump.RunAsync();
}

if (args.Contains("--plist-selftest"))
{
    return WinStream.Tools.RaopProbe.PlistSelfTest.Run();
}

if (args.Contains("--pair-persistent"))
{
    var directHost = ParseString(args, "--host") ?? nameFilter;
    if (directHost is not null)
    {
        // Exercises the shipping Core pairing code rather than a probe-local copy.
        var pairPort = ParseInt(args, "--port", 7000);
        var pin = ParseString(args, "--pin");
        Console.WriteLine($"== Persistent pair-setup {directHost}:{pairPort} ==");
        try
        {
            using (var pinClient = new System.Net.Sockets.TcpClient())
            {
                await pinClient.ConnectAsync(directHost, pairPort);
                await using var pinStream = pinClient.GetStream();
                await HkpPersistent.RequestPinDisplayAsync(pinStream, directHost, pairPort);
                Console.WriteLine("PAIR_PIN_START_OK — look at the Mac for a code");
            }

            using var pairClient = new System.Net.Sockets.TcpClient();
            await pairClient.ConnectAsync(directHost, pairPort);
            await using var pairStream = pairClient.GetStream();
            var credentials = await HkpPersistent.PairSetupAsync(
                pairStream,
                directHost,
                pairPort,
                _ => Task.FromResult(pin ?? Prompt("AirPlay code: ")));
            Console.WriteLine($"PAIR_SETUP_OK client={credentials.ClientPairingId} accessory={credentials.AccessoryPairingId}");
        }
        catch (PairingPinSkippedException)
        {
            Console.WriteLine("PAIR_SETUP_SKIPPED no code entered");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PAIR_SETUP_FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        return 0;
    }
}

static string? Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine();
}

// Masked so a receiver password never lands in the console scrollback that gets
// pasted into bug reports.
static string? PromptSecret(string label)
{
    Console.Write(label);
    var typed = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return typed.Length == 0 ? null : typed.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (typed.Length > 0)
            {
                typed.Length--;
                Console.Write("\b \b");
            }

            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            typed.Append(key.KeyChar);
            Console.Write('*');
        }
    }
}

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
        (d.DisplayName ?? string.Empty).Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
        (d.IPAddress ?? string.Empty).Equals(nameFilter, StringComparison.OrdinalIgnoreCase));

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

if (args.Contains("--pair"))
{
    Console.WriteLine($"\n== Transient HKP pair-setup {target.DisplayName} ({target.IPAddress}:{target.Port}) ==");
    try
    {
        using var pairing = await WinStream.Core.Protocol.AirPlay2.HkpPairSetupClient.PairAsync(
            target.IPAddress,
            target.Port);
        Console.WriteLine($"PAIR_OK sessionKeyLen={pairing.SessionKey.Count} shkLen={pairing.AudioSharedKey().Length}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PAIR_FAIL: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

if (args.Contains("--setup-diag"))
{
    // Prefer --prompt-password so the secret never lands in shell history.
    var receiverPassword = ParseString(args, "--password")
        ?? (args.Contains("--prompt-password")
            ? PromptSecret("AirPlay Receiver password: ")
            : null);
    return await WinStream.Tools.RaopProbe.SetupDiagnostics.RunAsync(target, receiverPassword);
}

if (args.Contains("--setup"))
{
    Console.WriteLine(
        $"\n== AP2 pair + encrypted GET /info + session SETUP {target.DisplayName} " +
        $"({target.IPAddress}:{target.Port}) ==");
    try
    {
        await using var control = new WinStream.Core.Protocol.AirPlay2.EncryptedRtspClient(
            target.IPAddress,
            target.Port);
        await control.ConnectAndPairAsync();
        await control.GetInfoAsync();
        await control.SessionSetupAsync();
        Console.WriteLine($"SESSION_SETUP_OK eventPort={control.EventPort}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SESSION_SETUP_FAIL: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

if (args.Contains("--stream"))
{
    // --shared-clock mimics StreamingOrchestrator, which overrides the session's
    // RTP timestamp with a fan-out clock stamp on every submit.
    var stream = () => RunAp2StreamAsync(target, seconds, args.Contains("--shared-clock"));
    return args.Contains("--ptp-listen")
        ? await WinStream.Tools.RaopProbe.PtpListen.RunAsync(stream)
        : await stream();
}

if (args.Contains("--idle"))
{
    return await RunAp2IdleAsync(target, seconds);
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

// Holds a session open without sending any RTP, to see whether the receiver
// tears down a silent sender.
static async Task<int> RunAp2IdleAsync(DeviceInfo target, int seconds)
{
    Console.WriteLine(
        $"\n== AP2 idle {seconds}s (no RTP) to {target.DisplayName} " +
        $"({target.IPAddress}:{target.Port}) ==");
    await using var ap2 = new AirPlay2Session(target);
    var stopwatch = Stopwatch.StartNew();
    ap2.StateChanged += (_, change) => Console.WriteLine(
        $"  {stopwatch.Elapsed.TotalSeconds,6:F2}s state| {change.Current}" +
        $"{(change.Reason is null ? "" : $" — {change.Reason}")}");

    try
    {
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ap2.ConnectAsync(connectCts.Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"IDLE_CONNECT_FAIL: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"  {stopwatch.Elapsed.TotalSeconds,6:F2}s connected; sending nothing");
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        await Task.Delay(250);
        if (ap2.State is SessionState.Failed or SessionState.Disconnected)
        {
            Console.WriteLine(
                $"IDLE_DROPPED after {stopwatch.Elapsed.TotalSeconds:F2}s state={ap2.State}");
            return 1;
        }
    }

    Console.WriteLine($"IDLE_SURVIVED {seconds}s state={ap2.State}");
    await ap2.DisconnectAsync();
    return 0;
}

static async Task<int> RunAp2StreamAsync(DeviceInfo target, int seconds, bool useSharedClock = false)
{
    Console.WriteLine(
        $"\n== AP2 stream {seconds}s to {target.DisplayName} ({target.IPAddress}:{target.Port}) " +
        $"sharedClock={useSharedClock} ==");
    await using var ap2 = new AirPlay2Session(target);
    var clock = new WinStream.Core.Streaming.PcmFanoutClock();
    var elapsed = Stopwatch.StartNew();
    ap2.StateChanged += (_, change) => Console.WriteLine(
        $"  {elapsed.Elapsed.TotalSeconds,6:F2}s state| {change.Current}" +
        $"{(change.Reason is null ? "" : $" — {change.Reason}")}");
    try
    {
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ap2.ConnectAsync(connectCts.Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"STREAM_CONNECT_FAIL: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    var format = new AudioFormat(44100, 2, 16);
    var stopwatch = Stopwatch.StartNew();
    var phase = 0.0;
    const double toneHz = 440.0;
    const int chunkFrames = 441;

    // Pace against an absolute frame schedule. Sleeping a fixed 10 ms per 10 ms
    // chunk underruns badly, because Windows rounds the sleep up to ~15.6 ms and
    // the receiver then sees every packet arrive late.
    var framesProduced = 0L;
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

        if (useSharedClock)
        {
            ap2.SubmitPcm(pcm, format, clock.Advance((uint)chunkFrames));
        }
        else
        {
            ap2.SubmitPcm(pcm, format);
        }

        framesProduced += chunkFrames;
        var due = TimeSpan.FromSeconds((double)framesProduced / format.SampleRate);
        var ahead = due - stopwatch.Elapsed;
        if (ahead > TimeSpan.Zero)
        {
            await Task.Delay(ahead);
        }

        if (ap2.State is SessionState.Failed or SessionState.Disconnected)
        {
            Console.WriteLine(
                $"STREAM_DROPPED after {elapsed.Elapsed.TotalSeconds:F2}s state={ap2.State}");
            return 1;
        }
    }

    await ap2.DisconnectAsync();
    Console.WriteLine($"STREAM_OK seconds={seconds} final={ap2.State}");
    return ap2.State == SessionState.Disconnected ? 0 : 1;
}

static int ParseInt(string[] args, string flag, int fallback)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
        ? value
        : fallback;
}

static string? ParseString(string[] args, string flag)
{
    var index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
