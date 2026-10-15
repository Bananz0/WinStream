using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Network;

namespace WinStream.Audio
{
    internal sealed class NodeAirTunesSender : IDisposable
    {
        private const string EnableEnvVar = "WINSTREAM_USE_NODE_AIRTUNES2";
        private const string NodePathEnvVar = "WINSTREAM_NODE_PATH";
        private const string SenderDirEnvVar = "WINSTREAM_NODE_AIRTUNES2_DIR";
        private const string JsonPrefix = "__WINSTREAM__";
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

        private readonly DeviceInfo _deviceInfo;
        private readonly bool _requireEnableFlag;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private Process _process;
        private TaskCompletionSource<bool> _readyTcs;
        private bool _stopping;

        public NodeAirTunesSender(DeviceInfo deviceInfo, bool requireEnableFlag = true)
        {
            _deviceInfo = deviceInfo;
            _requireEnableFlag = requireEnableFlag;
        }

        public string LastErrorMessage { get; private set; } = string.Empty;

        public static bool ShouldUseForDevice(DeviceInfo deviceInfo)
        {
            return IsEnabled();
        }

        public static bool TryResolveDependencies(out string message)
        {
            return TryResolveDependencies(out message, requireEnableFlag: true);
        }

        public static bool TryResolveDependencies(out string message, bool requireEnableFlag)
        {
            if (requireEnableFlag && !IsEnabled())
            {
                message = $"{EnableEnvVar} is not enabled.";
                return false;
            }

            if (!File.Exists(ResolveBridgeScriptPath()))
            {
                message = "Node AirPlay bridge script was not found in the WinStream output directory.";
                return false;
            }

            if (ResolveSenderDirectory() == null)
            {
                message =
                    $"node_airtunes2 source was not found. Set {SenderDirEnvVar} or place it under third_party/node_airtunes2.";
                return false;
            }

            message = string.Empty;
            return true;
        }

        public Task<bool> InitializeAsync()
        {
            var ok = TryResolveDependencies(out var message, _requireEnableFlag);
            LastErrorMessage = message;
            return Task.FromResult(ok);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_process != null)
            {
                return;
            }

            var senderDirectory = ResolveSenderDirectory();
            if (senderDirectory == null)
            {
                throw new InvalidOperationException(
                    $"Unable to locate node_airtunes2. Set {SenderDirEnvVar} to the extracted source directory.");
            }

            var bridgeScriptPath = ResolveBridgeScriptPath();
            var pin = AirPlay2AuthService.PeekConfiguredPin();
            var payload = new
            {
                host = _deviceInfo.IPAddress,
                port = _deviceInfo.Port,
                airplay2 = _deviceInfo.IsAirPlay2Device,
                txt = _deviceInfo.RawTxtRecords ?? Array.Empty<string>(),
                debug = true,
                forceAlac = true,
                volume = 50,
                passcode = string.IsNullOrWhiteSpace(pin) ? null : pin.Trim()
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable(NodePathEnvVar) ?? "node",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = senderDirectory
            };
            startInfo.ArgumentList.Add(bridgeScriptPath);
            startInfo.ArgumentList.Add(payloadBase64);
            startInfo.Environment[SenderDirEnvVar] = senderDirectory;

            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = false;
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += (_, _) =>
            {
                if (_stopping)
                {
                    _readyTcs.TrySetCanceled();
                    return;
                }

                var message = $"node_airtunes2 sender exited with code {_process.ExitCode}.";
                LastErrorMessage = message;
                Logger.LogMessage(message, "session");
                _readyTcs.TrySetException(new InvalidOperationException(message));
            };

            if (!_process.Start())
            {
                _process.Dispose();
                _process = null;
                throw new InvalidOperationException("Failed to launch node_airtunes2 sender process.");
            }

            _ = Task.Run(() => PumpReaderAsync(_process.StandardOutput, "stdout", cancellationToken));
            _ = Task.Run(() => PumpReaderAsync(_process.StandardError, "stderr", cancellationToken));

            using var registration = cancellationToken.Register(() => _readyTcs.TrySetCanceled(cancellationToken));
            var completed = await Task.WhenAny(_readyTcs.Task, Task.Delay(StartupTimeout, cancellationToken));
            if (completed != _readyTcs.Task)
            {
                LastErrorMessage = $"node_airtunes2 sender did not become ready within {StartupTimeout.TotalSeconds:F0}s.";
                await StopAsync();
                throw new TimeoutException(LastErrorMessage);
            }

            await _readyTcs.Task;
        }

        public async Task WriteAudioAsync(byte[] pcmData)
        {
            if (_process == null || _process.HasExited)
            {
                throw new InvalidOperationException("node_airtunes2 sender is not running.");
            }

            await _writeLock.WaitAsync();
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(pcmData, 0, pcmData.Length);
                await _process.StandardInput.BaseStream.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task StopAsync()
        {
            if (_process == null)
            {
                return;
            }

            _stopping = true;

            try
            {
                _process.StandardInput.Close();
            }
            catch
            {
            }

            try
            {
                await _process.WaitForExitAsync();
            }
            catch
            {
            }

            _process.Dispose();
            _process = null;
        }

        public void Dispose()
        {
            StopAsync().Wait(2000);
            _writeLock.Dispose();
        }

        private async Task PumpReaderAsync(StreamReader reader, string streamName, CancellationToken cancellationToken)
        {
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith(JsonPrefix, StringComparison.Ordinal))
                {
                    HandleStructuredMessage(line[JsonPrefix.Length..]);
                    continue;
                }

                Logger.LogMessage($"node_airtunes2 {streamName}: {line}", "session");
            }
        }

        private void HandleStructuredMessage(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                if (string.Equals(type, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    _readyTcs.TrySetResult(true);
                    Logger.LogMessage($"node_airtunes2 backend ready for {_deviceInfo.DisplayName}", "session");
                    return;
                }

                if (string.Equals(type, "need_password", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "pair_failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var message = root.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString()
                        : $"node_airtunes2 reported {type}";
                    LastErrorMessage = message ?? $"node_airtunes2 reported {type}";
                    _readyTcs.TrySetException(new InvalidOperationException(LastErrorMessage));
                    Logger.LogMessage(LastErrorMessage, "session");
                    return;
                }

                Logger.LogMessage($"node_airtunes2 event: {json}", "session");
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Failed to parse node_airtunes2 message: {ex.Message}", "session");
            }
        }

        private static bool IsEnabled()
        {
            var value = Environment.GetEnvironmentVariable(EnableEnvVar);
            return value != null &&
                   (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveBridgeScriptPath()
        {
            var rootsToProbe = new[]
            {
                GetAssemblyDirectory(),
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory()
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var root in rootsToProbe)
            {
                var current = new DirectoryInfo(root);
                while (current != null)
                {
                    var candidate = Path.Combine(current.FullName, "Node", "airtunes-bridge.js");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    var repoCandidate = Path.Combine(current.FullName, "WinStream", "Node", "airtunes-bridge.js");
                    if (File.Exists(repoCandidate))
                    {
                        return repoCandidate;
                    }

                    current = current.Parent;
                }
            }

            return Path.Combine(GetAssemblyDirectory(), "Node", "airtunes-bridge.js");
        }

        private static string ResolveSenderDirectory()
        {
            var configured = Environment.GetEnvironmentVariable(SenderDirEnvVar);
            if (!string.IsNullOrWhiteSpace(configured) &&
                File.Exists(Path.Combine(configured, "package.json")))
            {
                return configured;
            }

            var rootsToProbe = new[]
            {
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory()
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var root in rootsToProbe)
            {
                var current = new DirectoryInfo(root);
                while (current != null)
                {
                    var candidate = Path.Combine(current.FullName, "third_party", "node_airtunes2");
                    if (File.Exists(Path.Combine(candidate, "package.json")))
                    {
                        return candidate;
                    }

                    current = current.Parent;
                }
            }

            return null;
        }

        private static string GetAssemblyDirectory()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            return string.IsNullOrWhiteSpace(location)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        }
    }
}
