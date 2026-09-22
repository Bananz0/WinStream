using System.Net.Sockets;
using WinStream.Core.Audio;
using WinStream.Core.Logging;

namespace WinStream.Core.Protocol.Link;

/// <summary>
/// Authenticated TCP control channel to a Link companion. Media stays on UDP; this
/// connection lives as long as the session so STOP and telemetry stay available.
/// </summary>
public sealed class LinkControlClient : ILinkControlChannel
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly LinkControlLineChannel _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private LinkControlClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
        _channel = new LinkControlLineChannel(stream);
    }

    /// <summary>Null when the companion rejects the PIN; throws when it is unreachable.</summary>
    public static async Task<LinkControlClient?> ConnectAsync(
        string host,
        int controlPort,
        string pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, controlPort, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            var control = new LinkControlClient(client, stream);
            if (await control.HandshakeAsync(pin, cancellationToken).ConfigureAwait(false))
            {
                return control;
            }

            await control.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task StartAsync(
        int mediaPort,
        AudioFormat format,
        CancellationToken cancellationToken = default) =>
        ExpectOkAsync(LinkControlMessage.Start(mediaPort, format), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        ExpectOkAsync(LinkControlMessage.Stop, cancellationToken);

    public async Task<LinkReceiverTelemetry?> QueryTelemetryAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.WriteAsync(LinkControlMessage.Stat, cancellationToken).ConfigureAwait(false);
            var reply = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            return reply?.TryReadTelemetry(out var telemetry) == true ? telemetry : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _channel.WriteAsync(LinkControlMessage.Bye, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Info("link", $"Link control BYE not delivered: {ex.GetType().Name}");
        }

        _stream.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }

    private async Task<bool> HandshakeAsync(string pin, CancellationToken cancellationToken)
    {
        await _channel.WriteAsync(LinkControlMessage.Hello, cancellationToken).ConfigureAwait(false);
        var hello = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (hello?.Verb != LinkControlVerb.Ok)
        {
            return false;
        }

        await _channel.WriteAsync(LinkControlMessage.Pin(pin), cancellationToken).ConfigureAwait(false);
        var authenticated = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        return authenticated?.Verb == LinkControlVerb.Ok;
    }

    private async Task ExpectOkAsync(LinkControlMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _channel.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            var reply = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reply?.Verb != LinkControlVerb.Ok)
            {
                throw new InvalidOperationException(
                    $"Companion rejected {message.Verb}: {reply?.ToString() ?? "connection closed"}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
