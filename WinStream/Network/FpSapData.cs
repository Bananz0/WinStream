using System;
using System.IO;
using System.Reflection;

namespace WinStream.Network
{
    internal static class FpSapData
    {
        private const string SnapshotFileName = "FpSapSnapshot.b64";
        private static readonly Lazy<byte[]> SnapshotBytes = new(LoadSnapshotBytes);

        public static byte[] GetSnapshotBytes()
        {
            return SnapshotBytes.Value;
        }

        private static byte[] LoadSnapshotBytes()
        {
            var path = ResolveSnapshotPath();
            var base64 = File.ReadAllText(path).Trim();
            return Convert.FromBase64String(base64);
        }

        private static string ResolveSnapshotPath()
        {
            var outputPath = Path.Combine(GetAssemblyDirectory(), SnapshotFileName);
            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            var repoPath = Path.Combine(GetAssemblyDirectory(), "Network", SnapshotFileName);
            if (File.Exists(repoPath))
            {
                return repoPath;
            }

            throw new FileNotFoundException(
                $"Managed FairPlay snapshot {SnapshotFileName} was not found next to the application.",
                outputPath);
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
