using System.Net.Sockets;
using System.Text;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Encrypted AP2 event TCP channel. Keys are swapped vs control because the
/// receiver initiates writes on this reverse connection.
/// </summary>
public sealed class EventChannel : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private RtspCryptoStream? _crypto;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public event EventHandler<Exception>? Faulted;

    public async Task ConnectAsync(
        string host,
        int eventPort,
        byte[] eventsWriteKey,
        byte[] eventsReadKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(eventsWriteKey);
        ArgumentNullException.ThrowIfNull(eventsReadKey);
        await _tcp.ConnectAsync(host, eventPort, cancellationToken).ConfigureAwait(false);
        // Incoming events are encrypted with Events-Write; replies use Events-Read.
        _crypto = new RtspCryptoStream(
            _tcp.GetStream(),
            eventsReadKey,
            eventsWriteKey);
        // Keep-alive must outlive the Connect cancellation token (reconnect budgets
        // cancel that token after success). Own a private CTS until DisposeAsync.
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => KeepAliveLoopAsync(_loopCts.Token), CancellationToken.None);
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pending = new StringBuilder();
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunk = await _crypto!.ReadNextChunkAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (chunk.Length == 0)
                {
                    continue;
                }

                pending.Append(Encoding.ASCII.GetString(chunk));
                while (TryConsumeRequest(pending, out var cSeq))
                {
                    // Bare 200 only — Content-Length / Audio-Latency on this reply
                    // corrupts the receiver realtime timeline (akustikrausch #90).
                    var reply = string.IsNullOrEmpty(cSeq)
                        ? "RTSP/1.0 200 OK\r\nServer: AirTunes/550.10\r\n\r\n"
                        : $"RTSP/1.0 200 OK\r\nServer: AirTunes/550.10\r\nCSeq: {cSeq}\r\n\r\n";
                    await _crypto.WritePlaintextAsync(
                            Encoding.ASCII.GetBytes(reply),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on dispose.
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    /// <summary>Parse one complete RTSP request from the buffer; echo its CSeq.</summary>
    public static bool TryConsumeRequest(StringBuilder pending, out string? cSeq)
    {
        cSeq = null;
        var text = pending.ToString();
        var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0)
        {
            return false;
        }

        var header = text[..headEnd];
        var contentLength = 0;
        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var len) &&
                len > 0)
            {
                contentLength = len;
            }
            else if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
            {
                cSeq = value;
            }
        }

        var total = headEnd + 4 + contentLength;
        if (text.Length < total)
        {
            return false;
        }

        pending.Remove(0, total);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // Ignore keep-alive faults during teardown.
            }
        }

        _crypto?.Dispose();
        _tcp.Dispose();
        _loopCts?.Dispose();
        _disposed = true;
    }
}
