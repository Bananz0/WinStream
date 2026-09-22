namespace WinStream.Core.Protocol.Link;

/// <summary>Port derivation for the Link control plane (media stays on UDP).</summary>
public static class LinkControlProtocol
{
    public const int DefaultControlPortOffset = 1;

    public static int DefaultControlPort =>
        Wsl1Constants.DefaultMediaPort + DefaultControlPortOffset;

    /// <summary>
    /// Authenticates and immediately hangs up. Callers that will stream should hold a
    /// <see cref="LinkControlClient"/> instead so STOP and telemetry stay reachable.
    /// </summary>
    public static async Task<bool> HandshakeAsync(
        string host,
        int controlPort,
        string pin,
        CancellationToken cancellationToken = default)
    {
        var client = await LinkControlClient.ConnectAsync(host, controlPort, pin, cancellationToken)
            .ConfigureAwait(false);
        if (client is null)
        {
            return false;
        }

        await client.DisposeAsync().ConfigureAwait(false);
        return true;
    }
}
