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

    public static event EventHandler<string>? LineWritten;

    public static IReadOnlyList<string> Snapshot() => Recent.ToArray();

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

        LineWritten?.Invoke(null, line);
    }

    private static string Sanitize(string message)
    {
        // Defense in depth against accidental secret material in messages.
        if (message.Contains("rsaaeskey", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("aesiv", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("BEGIN RSA", StringComparison.OrdinalIgnoreCase))
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
