using WinStream.Core.Logging;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Decides which pairing path produces the control keys: stored identity first,
/// then a one-time PIN pair-setup, then transient (Accept every time).
/// </summary>
/// <remarks>
/// Kept free of sockets so the fallback order and the persistence callbacks can be
/// unit-tested; <see cref="EncryptedRtspClient"/> supplies the transport handlers.
/// </remarks>
public sealed class PairingKeyNegotiator
{
    public delegate Task<AirPlayControlKeys> VerifyHandler(
        PairingCredentials credentials,
        CancellationToken cancellationToken);

    public delegate Task<PairingCredentials> SetupHandler(
        Func<CancellationToken, Task<string?>> requestPinAsync,
        CancellationToken cancellationToken);

    public delegate Task<AirPlayControlKeys> TransientHandler(CancellationToken cancellationToken);

    private readonly PairingOptions? _options;
    private readonly Action _resetTransport;

    public PairingKeyNegotiator(PairingOptions? options, Action resetTransport)
    {
        ArgumentNullException.ThrowIfNull(resetTransport);
        _options = options;
        _resetTransport = resetTransport;
    }

    public async Task<AirPlayControlKeys> NegotiateAsync(
        VerifyHandler verify,
        SetupHandler setup,
        TransientHandler transient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(transient);

        var stored = _options?.StoredCredentials;
        if (stored is { IsComplete: true })
        {
            try
            {
                return await verify(stored, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (NotCancellation(ex, cancellationToken))
            {
                AppLog.Warn(
                    "pair",
                    $"Stored pairing failed; clearing and retrying: {ex.GetType().Name}: {ex.Message}");
                _options?.OnStoredCredentialsRejected?.Invoke();
                _resetTransport();
            }
        }

        var requestPin = _options?.RequestPinAsync;
        if (requestPin is not null)
        {
            var persisted = false;
            try
            {
                var credentials = await setup(requestPin, cancellationToken).ConfigureAwait(false);
                _options?.OnPaired?.Invoke(credentials);
                persisted = true;
                return await verify(credentials, cancellationToken).ConfigureAwait(false);
            }
            catch (PairingPinSkippedException)
            {
                AppLog.Info("pair", "AirPlay code skipped; using transient pairing");
                _resetTransport();
            }
            catch (Exception ex) when (NotCancellation(ex, cancellationToken))
            {
                AppLog.Warn(
                    "pair",
                    $"Persistent pairing failed; using transient: {ex.GetType().Name}: {ex.Message}");

                // Only an identity that was already handed to the store can be stale.
                // A setup failure never wrote one, so clearing then would drop a good
                // pairing from an earlier session.
                if (persisted)
                {
                    _options?.OnStoredCredentialsRejected?.Invoke();
                }

                _resetTransport();
            }
        }

        // Reported only once transient actually produced keys: a failed fallback ends
        // the connect attempt, and marking the receiver then would claim a temporary
        // pairing that never existed.
        var keys = await transient(cancellationToken).ConfigureAwait(false);
        _options?.OnTransientPairing?.Invoke();
        return keys;
    }

    /// <summary>
    /// A cancelled connect must abort, not silently downgrade to the transient
    /// pairing the user is trying to get away from.
    /// </summary>
    private static bool NotCancellation(Exception ex, CancellationToken cancellationToken) =>
        !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested);
}
