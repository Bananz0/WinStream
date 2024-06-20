using System;
using System.Diagnostics;
using System.IO;

namespace WinStream.Network
{
    public static class Logger
    {
        public static void LogException(Exception ex)
        {
            var logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStream", "Logs", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));
            File.AppendAllText(logFilePath, $"{DateTime.Now}: {ex}{Environment.NewLine}");
            Debug.WriteLine($"Exception logged to file: {logFilePath}");
        }
    }
}
