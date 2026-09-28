using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace WinStream.Core.Logging;

/// <summary>
/// Structured local logger. Never log keys, samples, public keys, or other PII.
/// </summary>
/// <remarks>
/// <see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> append to the
/// in-memory snapshot synchronously on the calling thread — callers on
/// realtime send/keep-alive threads can rely on that. The file write is the
/// only part that leaves the calling thread: it is handed to a bounded
/// background channel (capacity 1024, drop-oldest when full) drained by one
/// dedicated writer thread, so a slow or stalled disk never blocks a caller.
/// </remarks>
public static class AppLog
{
    private static readonly ConcurrentQueue<string> Recent = new();
    private const int MaxRecent = 200;
    private const int RetainDays = 7;
    private const int FileChannelCapacity = 1024;

    private static readonly object FileGate = new();
    private static string? _filePath;
    private static StreamWriter? _writer;

    private static readonly Channel<Action> FileWriteChannel = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(FileChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    static AppLog()
    {
        var writerThread = new Thread(FileWriterLoop)
        {
            IsBackground = true,
            Name = "AppLog.FileWriter",
        };
        writerThread.Start();
    }

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
                _writer?.Dispose();
                _writer = new StreamWriter(
                    new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
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

    /// <summary>Stops appending to the file sink (tests should call this before deleting the directory).</summary>
    public static void DisableFileSink()
    {
        // Make sure nothing already queued for the previous sink lands on the
        // next one (or gets written after the writer is nulled out below).
        DrainFileWrites();

        lock (FileGate)
        {
            _writer?.Dispose();
            _writer = null;
            _filePath = null;
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

        // In-memory snapshot: synchronous on the calling thread, always.
        Recent.Enqueue(line);
        while (Recent.Count > MaxRecent && Recent.TryDequeue(out _))
        {
        }

        EnqueueFileWrite(line);
        LineWritten?.Invoke(null, line);
    }

    /// <summary>
    /// Hands the file write off to the background channel. Never blocks the
    /// caller: a full channel drops the oldest queued write rather than
    /// waiting, and the enqueue itself cannot throw for a full/closed channel
    /// under <see cref="BoundedChannelFullMode.DropOldest"/>.
    /// </summary>
    private static void EnqueueFileWrite(string line)
    {
        FileWriteChannel.Writer.TryWrite(() => AppendToFile(line));
    }

    private static void FileWriterLoop()
    {
        var reader = FileWriteChannel.Reader;
        while (true)
        {
            Action action;
            try
            {
                action = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                action();
            }
            catch
            {
                // A queued action must never take down the writer thread.
            }
        }
    }

    private static void AppendToFile(string line)
    {
        lock (FileGate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
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

    /// <summary>
    /// Blocks until every file write enqueued so far has been drained by the
    /// background writer thread. Used internally before swapping/closing the
    /// sink, and by tests that assert on file contents (which otherwise have
    /// no deterministic point to read from, since the write is asynchronous).
    /// </summary>
    internal static bool DrainFileWrites(TimeSpan? timeout = null)
    {
        using var signal = new ManualResetEventSlim(false);
        if (!FileWriteChannel.Writer.TryWrite(() => signal.Set()))
        {
            return false;
        }

        return signal.Wait(timeout ?? TimeSpan.FromSeconds(5));
    }

    // Test seam.
    private static TimeProvider _timeProvider = TimeProvider.System;

    internal static void SetTimeProvider(TimeProvider timeProvider) =>
        _timeProvider = timeProvider;
}
