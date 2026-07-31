using System.Collections.Concurrent;
using System.Text;

namespace WinStream.Core.Logging;

/// <summary>
/// Structured local logger. Never log keys, samples, public keys, or other PII.
/// </summary>
public static class AppLog
{
    private static readonly ConcurrentQueue<string> Recent = new();
    private const int MaxRecent = 200;
    private const int RetainDays = 7;

    private static readonly object FileGate = new();
    private static string? _filePath;

    public static event EventHandler<string>? LineWritten;

    public static IReadOnlyList<string> Snapshot() => Recent.ToArray();

    /// <summary>Path of the active log file, or null when only buffering in memory.</summary>
    public static string? FilePath
    {
        get
        {
            lock (FileGate)
            {
                return _filePath;
            }
        }
    }

    /// <summary>
    /// Starts appending to a dated file under <paramref name="directory"/>. Opt-in so
    /// tests and library consumers stay side-effect free.
    /// </summary>
    public static string? EnableFileSink(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"winstream-{_timeProvider.GetUtcNow():yyyyMMdd}.log");

            lock (FileGate)
            {
                _filePath = path;
            }

            PruneOldLogs(directory);
            Info("log", $"File logging started: {path}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
            return null;
        }
    }

    private static void PruneOldLogs(string directory)
    {
        var cutoff = _timeProvider.GetUtcNow().AddDays(-RetainDays);
        foreach (var file in Directory.EnumerateFiles(directory, "winstream-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the file; pruning is best effort.
            }
        }
    }

    public static void Info(string category, string message) =>
        Write("INFO", category, message);

    public static void Warn(string category, string message) =>
        Write("WARN", category, message);

    public static void Error(string category, string message) =>
        Write("ERROR", category, message);

    private static void Write(string level, string category, string message)
    {
        var line = new StringBuilder(128)
            .Append(_timeProvider.GetUtcNow().ToString("O"))
            .Append(' ')
            .Append(level)
            .Append(' ')
            .Append(category)
            .Append(' ')
            .Append(Sanitize(message))
            .ToString();

        Recent.Enqueue(line);
        while (Recent.Count > MaxRecent && Recent.TryDequeue(out _))
        {
        }

        AppendToFile(line);
        LineWritten?.Invoke(null, line);
    }

    private static void AppendToFile(string line)
    {
        lock (FileGate)
        {
            if (_filePath is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let logging break the caller.
            }
        }
    }

    private static string Sanitize(string message)
    {
        // Defense in depth against accidental secret material in messages.
        if (message.Contains("rsaaeskey", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("aesiv", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("BEGIN RSA", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("shk", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("sessionkey", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("session key", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted]";
        }

        return message;
    }

    // Test seam.
    private static TimeProvider _timeProvider = TimeProvider.System;

    internal static void SetTimeProvider(TimeProvider timeProvider) =>
        _timeProvider = timeProvider;
}
