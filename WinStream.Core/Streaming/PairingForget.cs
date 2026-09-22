using WinStream.Core.Logging;
using WinStream.Core.Persistence;

namespace WinStream.Core.Streaming;

/// <summary>
/// User-initiated reset of a single receiver's stored HomeKit identity.
/// </summary>
public static class PairingForget
{
    /// <summary>
    /// True when the store holds a complete identity for <paramref name="receiverKey"/>.
    /// </summary>
    public static bool HasStored(IPairingCredentialStore store, string receiverKey)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);
        return store.TryGet(receiverKey, out _);
    }

    /// <summary>
    /// Removes stored credentials for <paramref name="receiverKey"/>.
    /// </summary>
    /// <returns><c>true</c> when credentials were present and removed.</returns>
    public static bool Forget(IPairingCredentialStore store, string receiverKey)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);

        if (!store.TryGet(receiverKey, out _))
        {
            AppLog.Info("pair", $"No stored pairing to forget for {receiverKey}");
            return false;
        }

        store.Remove(receiverKey);
        AppLog.Info("pair", $"Forgot stored pairing for {receiverKey}");
        return true;
    }
}
