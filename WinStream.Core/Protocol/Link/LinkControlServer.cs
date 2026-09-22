using System.Net.Sockets;
using WinStream.Core.Logging;

namespace WinStream.Core.Protocol.Link;

/// <summary>
/// Receiver side of the Link control plane. One sender at a time: a Link companion
/// renders a single stream, so a second caller waits rather than interleaving.
/// </summary>
public static class LinkControlServer
{
    /// <summary>Serves connections until cancelled; one connection failing never stops the loop.</summary>
    public static async Task ServeAsync(
        TcpListener listener,
        string expectedPin,
        ILinkControlHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPin);
        ArgumentNullException.ThrowIfNull(handler);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await using var stream = client.GetStream();
                await ServeConnectionAsync(stream, expectedPin, handler, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Info("link", $"Link control connection ended: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    /// <summary>Returns when the peer disconnects, says BYE, or fails authentication.</summary>
    public static async Task ServeConnectionAsync(
        Stream stream,
        string expectedPin,
        ILinkControlHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPin);
        ArgumentNullException.ThrowIfNull(handler);

        var channel = new LinkControlLineChannel(stream);
        if (!await AuthenticateAsync(channel, expectedPin, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (message is null || message.Value.Verb == LinkControlVerb.Bye)
            {
                await handler.OnStopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var reply = await DispatchAsync(message.Value, handler, cancellationToken)
                .ConfigureAwait(false);
            await channel.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> AuthenticateAsync(
        LinkControlLineChannel channel,
        string expectedPin,
        CancellationToken cancellationToken)
    {
        var hello = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (hello?.Verb != LinkControlVerb.Hello)
        {
            await channel.WriteAsync(LinkControlMessage.Fail("expected HELLO"), cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await channel.WriteAsync(LinkControlMessage.Ok, cancellationToken).ConfigureAwait(false);

        var pin = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        // Ordinal, full-string equality: a prefix of the PIN must never authenticate.
        if (pin?.Verb != LinkControlVerb.Pin ||
            !string.Equals(pin.Value.Argument, expectedPin, StringComparison.Ordinal))
        {
            await channel.WriteAsync(LinkControlMessage.Fail("bad pin"), cancellationToken)
                .ConfigureAwait(false);
            AppLog.Warn("link", "Link control rejected a PIN.");
            return false;
        }

        await channel.WriteAsync(LinkControlMessage.Ok, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<LinkControlMessage> DispatchAsync(
        LinkControlMessage message,
        ILinkControlHandler handler,
        CancellationToken cancellationToken)
    {
        switch (message.Verb)
        {
            case LinkControlVerb.Start:
                if (!message.TryReadStart(out var mediaPort, out var format))
                {
                    return LinkControlMessage.Fail("bad start");
                }

                await handler.OnStartAsync(mediaPort, format, cancellationToken).ConfigureAwait(false);
                return LinkControlMessage.Ok;

            case LinkControlVerb.Stop:
                await handler.OnStopAsync(cancellationToken).ConfigureAwait(false);
                return LinkControlMessage.Ok;

            case LinkControlVerb.Stat:
                return LinkControlMessage.Telemetry(handler.GetTelemetry());

            case LinkControlVerb.Hello:
            case LinkControlVerb.Pin:
                return LinkControlMessage.Fail("already authenticated");

            default:
                return LinkControlMessage.Fail("unknown verb");
        }
    }
}
