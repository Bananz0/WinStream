using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Core.Streaming;

/// <summary>
/// Builds <see cref="PairingOptions"/> the orchestrator and tests share: store
/// callbacks and the transient-pairing mark. Callers own the session map and must
/// clear any stale transient mark themselves before calling <see cref="Create"/> —
/// this factory only builds options, it does not mutate session state.
/// </summary>
public static class PairingOptionsFactory
{
    public static PairingOptions Create(
        IPairingCredentialStore store,
        string receiverKey,
        Func<CancellationToken, Task<string?>>? requestPinAsync = null,
        Action? markTransient = null,
        string? receiverPassword = null,
        bool isAutomatic = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);

        // An unattended background reconnect must never pop a blocking PIN dialog
        // with nobody present to answer it. A saved password still answers the
        // SETUP Digest 401 without a prompt, so only the password-less, PIN-only
        // path is gated here; if pairing needs the callback anyway, the attempt
        // fails and is surfaced as a Config-kind status instead.
        var requestPin = isAutomatic && string.IsNullOrEmpty(receiverPassword)
            ? null
            : requestPinAsync;

        // Password and persistent identity are independent: the password is the
        // transient SRP secret and answers SETUP Digest 401s after pair-verify.
        // Skipping identity for password Macs forced Accept on every Connect.
        return new PairingOptions
        {
            StoredCredentials = store.TryGet(receiverKey, out var stored) ? stored : null,
            RequestPinAsync = requestPin,
            ReceiverPassword = string.IsNullOrEmpty(receiverPassword) ? null : receiverPassword,
            OnPaired = credentials => store.Save(receiverKey, credentials),
            OnStoredCredentialsRejected = () => store.Remove(receiverKey),
            OnTransientPairing = markTransient
        };
    }
}
