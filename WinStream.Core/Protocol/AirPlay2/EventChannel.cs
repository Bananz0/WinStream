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
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunk = await _crypto!.ReadNextChunkAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (chunk.Length == 0)
                {
                    continue;
                }

                var text = Encoding.ASCII.GetString(chunk);
                if (text.Contains("POST /command", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("POST /feedback", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var reply = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 0\r\n\r\n");
                    await _crypto.WritePlaintextAsync(reply, cancellationToken)
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
