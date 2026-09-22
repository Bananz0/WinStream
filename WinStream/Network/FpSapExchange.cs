using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WinStream.Network
{
    internal static class FpSapExchange
    {
        private static readonly Lazy<byte[]> SnapshotBytes = new(FpSapData.GetSnapshotBytes);
        private const int HelperTimeoutMs = 30000;

        public static byte[] ComputeM3(byte[] m2)
        {
            ArgumentNullException.ThrowIfNull(m2);

            if (m2.Length < 12)
            {
                throw new ArgumentException("FairPlay M2 must contain at least the 12-byte FPLY header.", nameof(m2));
            }

            if (TryComputeM3WithHelper(m2, out var m3, out var error))
            {
                return m3;
            }

            _ = SnapshotBytes.Value;
            throw new NotSupportedException(
                $"Managed FairPlay SAP exchange emulator is not fully ported yet, and the helper failed: {error}");
        }

        private static bool TryComputeM3WithHelper(byte[] m2, out byte[] m3, out string error)
        {
            m3 = Array.Empty<byte>();
            error = string.Empty;

            var helperPath = ResolveHelperPath();
            if (helperPath == null)
            {
                error = "winstream-fpsap helper executable was not found.";
                return false;
            }

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    Arguments = Convert.ToHexString(m2),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                if (!process.Start())
                {
                    error = "helper process did not start.";
                    return false;
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(HelperTimeoutMs))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    error = $"helper timed out after {HelperTimeoutMs} ms.";
                    return false;
                }

                var stdout = stdoutTask.GetAwaiter().GetResult().Trim();
                var stderr = stderrTask.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0)
                {
                    error = string.IsNullOrWhiteSpace(stderr)
                        ? $"helper exited with code {process.ExitCode}."
                        : stderr;
                    return false;
                }

                m3 = Convert.FromHexString(stdout);
                if (m3.Length != PlayFair.M3RequestLength)
                {
                    error = $"helper returned {m3.Length} bytes, expected {PlayFair.M3RequestLength}.";
                    m3 = Array.Empty<byte>();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ResolveHelperPath()
        {
            var archName = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x64",
                _ => "x64",
            };

            var helperName = $"winstream-fpsap-{archName}.exe";
            var baseDirectory = GetAssemblyDirectory();
            var outputPath = Path.Combine(baseDirectory, "Native", "FairPlay", helperName);
            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            var sourcePath = Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "Native",
                "FairPlay",
                helperName);
            sourcePath = Path.GetFullPath(sourcePath);
            if (File.Exists(sourcePath))
            {
                return sourcePath;
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
