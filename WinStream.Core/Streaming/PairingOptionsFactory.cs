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
        string? receiverPassword = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);

        // Password and persistent identity are independent: the password is the
        // transient SRP secret and answers SETUP Digest 401s after pair-verify.
        // Skipping identity for password Macs forced Accept on every Connect.
        return new PairingOptions
        {
            StoredCredentials = store.TryGet(receiverKey, out var stored) ? stored : null,
            RequestPinAsync = requestPinAsync,
            ReceiverPassword = string.IsNullOrEmpty(receiverPassword) ? null : receiverPassword,
            OnPaired = credentials => store.Save(receiverKey, credentials),
            OnStoredCredentialsRejected = () => store.Remove(receiverKey),
            OnTransientPairing = markTransient
        };
    }
}
