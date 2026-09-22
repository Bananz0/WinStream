using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinStream.Core.Drivers;
using WinStream.Core.Audio;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tools.VadProbe;

/// <summary>
/// Measures what the WinStream virtual audio endpoint actually delivers, and writes
/// the result as evidence the SLA gate can read.
///
/// The declared engine period is a driver claim. The measured callback p95 is the
/// only number allowed to satisfy <see cref="LinkSlaEligibility"/>.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var seconds = 30;
        var outputPath = Path.Combine("artifacts", "driver", "vad-probe.json");
        string? deviceIdOverride = null;
        var listOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed):
                    seconds = parsed;
                    i++;
                    break;
                case "--out" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
                case "--device" when i + 1 < args.Length:
                    deviceIdOverride = args[++i];
                    break;
                case "--list":
                    listOnly = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
            }
        }

        if (seconds < 5)
        {
            Console.Error.WriteLine("--seconds must be at least 5; a shorter run cannot support a p95.");
            return 2;
        }

        using var enumerator = new MMDeviceEnumerator();

        if (listOnly)
        {
            ListEndpoints(enumerator);
            return 0;
        }

        var device = ResolveDevice(enumerator, deviceIdOverride);
        if (device is null)
        {
            Console.Error.WriteLine(
                $"No render endpoint named '{WinStreamVadIdentity.FriendlyName}' was found.");
            Console.Error.WriteLine("Install the driver first, or pass --device <id>. Use --list to see endpoints.");
            return 3;
        }

        Console.WriteLine($"Endpoint : {device.FriendlyName}");
        Console.WriteLine($"Id       : {device.ID}");

        var isOwned = IsOwnedWinStreamEndpoint(device);
        Console.WriteLine($"Owned VAD: {(isOwned ? "yes" : "NO — not the WinStream driver")}");

        var declared = ReadDeclaredPeriods(device);
        var measured = await MeasureCallbacksAsync(device, seconds);

        // An idle endpoint delivers empty buffers at a lazy poll rate, which says nothing
        // about the capture period. Refuse to treat a silent run as evidence.
        var carriedAudio = measured.SampleCount > 0 &&
            measured.SilentCallbacks < measured.SampleCount / 2;

        var slaCapable = isOwned
            && carriedAudio
            && LinkSlaEligibility.IsMeasuredCaptureSlaCapable(measured.P95Milliseconds);

        var report = new ProbeReport
        {
            SchemaVersion = 1,
            CapturedUtc = DateTimeOffset.UtcNow,
            Machine = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            EndpointId = device.ID,
            EndpointFriendlyName = device.FriendlyName,
            IsOwnedWinStreamEndpoint = isOwned,
            Declared = declared,
            Measured = measured,
            CarriedAudio = carriedAudio,
            MaxCaptureContributionMs = LinkSlaEligibility.MaxCaptureContributionMs,
            CaptureSlaCapable = slaCapable
        };

        WriteReport(report, outputPath);

        Console.WriteLine();
        Console.WriteLine($"Declared minimum period : {Describe(declared)}");
        Console.WriteLine($"Measured callback p95   : {measured.P95Milliseconds} ms " +
                          $"(mean {measured.MeanMilliseconds:F2} ms over {measured.SampleCount} callbacks)");
        Console.WriteLine($"Gate ({LinkSlaEligibility.MaxCaptureContributionMs} ms max)        : " +
                          (slaCapable ? "PASS" : "FAIL"));
        Console.WriteLine($"Report written to       : {Path.GetFullPath(outputPath)}");

        if (!carriedAudio)
        {
            Console.WriteLine();
            Console.WriteLine($"{measured.SilentCallbacks} of {measured.SampleCount} callbacks were empty.");
            Console.WriteLine("An idle endpoint polls lazily, so this run is not evidence of anything.");
            Console.WriteLine("Play audio to the endpoint for the whole run and measure again.");
        }
        else if (!slaCapable)
        {
            Console.WriteLine();
            Console.WriteLine("Capture is not SLA-capable, so the 8-10 ms badge stays off. That is the");
            Console.WriteLine("correct outcome unless the owned driver really is delivering <= 3 ms.");
        }

        return slaCapable ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            VadProbe — measure the WinStream virtual audio endpoint's real capture period.

              --seconds <n>   Measurement duration, minimum 5 (default 30)
              --out <path>    Report path (default artifacts/driver/vad-probe.json)
              --device <id>   Probe a specific endpoint id instead of the WinStream VAD
              --list          List render endpoints and exit

            Exit code 0 means measured capture met the SLA gate; 1 means it did not.
            """);
    }

    private static void ListEndpoints(MMDeviceEnumerator enumerator)
    {
        foreach (var candidate in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            Console.WriteLine($"{candidate.ID}  {candidate.FriendlyName}");
        }
    }

    private static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return endpoints.FirstOrDefault(d =>
                string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase));
        }

        return endpoints.FirstOrDefault(d =>
            d.FriendlyName.Contains(WinStreamVadIdentity.FriendlyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A friendly-name match is not identity. Confirm the endpoint belongs to a device
    /// whose enumerator is the WinStream root device before calling it owned.
    /// </summary>
    private static bool IsOwnedWinStreamEndpoint(MMDevice device)
    {
        try
        {
            var instanceId = device.Properties.Contains(PropertyKeys.DeviceInstanceId)
                ? device.Properties[PropertyKeys.DeviceInstanceId].Value as string
                : null;

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return false;
            }

            return instanceId.StartsWith(
                WinStreamVadIdentity.RootHardwareId,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static DeclaredPeriods ReadDeclaredPeriods(MMDevice device)
    {
        if (!AudioClient3Interop.TryGetEnginePeriods(device.ID, out var periods, out var error))
        {
            return new DeclaredPeriods { Available = false, Error = error };
        }

        return new DeclaredPeriods
        {
            Available = true,
            SampleRate = periods.MixSampleRate,
            Channels = periods.MixChannels,
            DefaultFrames = periods.DefaultFrames,
            FundamentalFrames = periods.FundamentalFrames,
            MinimumFrames = periods.MinimumFrames,
            MaximumFrames = periods.MaximumFrames,
            DefaultMilliseconds = Math.Round(periods.DefaultMilliseconds, 3),
            MinimumMilliseconds = Math.Round(periods.MinimumMilliseconds, 3)
        };
    }

    private static string Describe(DeclaredPeriods declared) =>
        declared.Available
            ? $"{declared.MinimumMilliseconds:F2} ms ({declared.MinimumFrames} frames at {declared.SampleRate} Hz)"
            : $"unavailable ({declared.Error})";

    private static async Task<MeasuredCallbacks> MeasureCallbacksAsync(MMDevice device, int seconds)
    {
        Console.WriteLine();
        Console.WriteLine($"Measuring capture callbacks for {seconds}s. Play audio to this endpoint now.");

        var measurer = new CaptureCallbackMeasurer();
        var intervals = new List<double>();
        var frequency = (double)Stopwatch.Frequency;
        long previous = 0;
        long silentCallbacks = 0;

        using var capture = new WasapiLoopbackCapture(device);
        using var finished = new SemaphoreSlim(0, 1);

        capture.DataAvailable += (_, e) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (previous != 0)
            {
                var delta = now - previous;
                measurer.RecordInterval(delta);
                intervals.Add(delta * 1000.0 / frequency);
            }

            previous = now;

            if (e.BytesRecorded == 0)
            {
                silentCallbacks++;
            }
        };

        capture.RecordingStopped += (_, _) =>
        {
            if (finished.CurrentCount == 0)
            {
                finished.Release();
            }
        };

        capture.StartRecording();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            capture.StopRecording();
            await finished.WaitAsync(TimeSpan.FromSeconds(5));
        }

        if (intervals.Count == 0)
        {
            return new MeasuredCallbacks { SampleCount = 0 };
        }

        var ordered = intervals.ToArray();
        Array.Sort(ordered);

        return new MeasuredCallbacks
        {
            SampleCount = ordered.Length,
            DurationSeconds = seconds,
            SilentCallbacks = silentCallbacks,
            MeanMilliseconds = Math.Round(intervals.Average(), 3),
            P50Milliseconds = Math.Round(Percentile(ordered, 0.50), 3),
            P95Milliseconds = measurer.MeasuredContributionMilliseconds,
            P95RawMilliseconds = Math.Round(Percentile(ordered, 0.95), 3),
            P99Milliseconds = Math.Round(Percentile(ordered, 0.99), 3),
            MaxMilliseconds = Math.Round(ordered[^1], 3)
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(sorted.Length * percentile) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private static void WriteReport(ProbeReport report, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(report, ProbeJson.Options);
        File.WriteAllText(path, json);
    }
}

internal static class PropertyKeys
{
    internal static readonly PropertyKey DeviceInstanceId = new()
    {
        formatId = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
        propertyId = 256
    };
}

internal static class ProbeJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class ProbeReport
{
    public int SchemaVersion { get; init; }

    public DateTimeOffset CapturedUtc { get; init; }

    public string Machine { get; init; } = string.Empty;

    public string OsVersion { get; init; } = string.Empty;

    public string EndpointId { get; init; } = string.Empty;

    public string EndpointFriendlyName { get; init; } = string.Empty;

    public bool IsOwnedWinStreamEndpoint { get; init; }

    public DeclaredPeriods Declared { get; init; } = new();

    public MeasuredCallbacks Measured { get; init; } = new();

    /// <summary>False when the run was mostly empty buffers, which invalidates it.</summary>
    public bool CarriedAudio { get; init; }

    public int MaxCaptureContributionMs { get; init; }

    public bool CaptureSlaCapable { get; init; }
}

internal sealed class DeclaredPeriods
{
    public bool Available { get; init; }

    public string? Error { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public int DefaultFrames { get; init; }

    public int FundamentalFrames { get; init; }

    public int MinimumFrames { get; init; }

    public int MaximumFrames { get; init; }

    public double DefaultMilliseconds { get; init; }

    public double MinimumMilliseconds { get; init; }
}

internal sealed class MeasuredCallbacks
{
    public int SampleCount { get; init; }

    public int DurationSeconds { get; init; }

    public long SilentCallbacks { get; init; }

    public double MeanMilliseconds { get; init; }

    public double P50Milliseconds { get; init; }

    /// <summary>Rounded up through the shared policy — this is the gate input.</summary>
    public int P95Milliseconds { get; init; }

    public double P95RawMilliseconds { get; init; }

    public double P99Milliseconds { get; init; }

    public double MaxMilliseconds { get; init; }
}
