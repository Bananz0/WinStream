using WinStream.Core.Logging;
using WinStream.Core.Persistence;

namespace WinStream.Core.Streaming;

/// <summary>Thrown when a receiver requires a password but none was supplied.</summary>
public sealed class ReceiverPasswordRequiredException : InvalidOperationException
{
    public ReceiverPasswordRequiredException()
        : base(ConnectionFailureCopy.PasswordRequiredDetail)
    {
    }
}

/// <summary>
/// Resolves the password policy independently of WinUI: no password for open
/// receivers, then stored credentials, then one prompt, otherwise a typed failure.
/// </summary>
public static class ReceiverPasswordResolver
{
    public static async Task<string?> ResolveAsync(
        IReceiverPasswordStore store,
        string receiverKey,
        bool requiresPassword,
        Func<string, CancellationToken, Task<string?>>? promptAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);

        if (!requiresPassword)
        {
            return null;
        }

        if (store.TryGet(receiverKey, out var stored) &&
            !string.IsNullOrWhiteSpace(stored))
        {
            AppLog.Info("password", $"Using saved password for {receiverKey}");
            return stored;
        }

        if (promptAsync is null)
        {
            AppLog.Warn("password", $"No password prompt wired for {receiverKey}");
            throw new ReceiverPasswordRequiredException();
        }

        // The dialog task itself resolves to empty on cancel (WinUI closes it via
        // a token.Register callback), but that happens on the UI thread's own
        // schedule. WaitAsync makes a cancelled connect stop waiting immediately
        // instead of racing a joining caller for whatever the dialog eventually
        // returns.
        var prompted = await promptAsync(receiverKey, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prompted))
        {
            return prompted.Trim();
        }

        AppLog.Info("password", $"Prompt returned no password for {receiverKey}");
        throw new ReceiverPasswordRequiredException();
    }

    /// <summary>Store-only lookup for reconnects that must never display UI.</summary>
    public static string? StoredOrNull(
        IReceiverPasswordStore store,
        string receiverKey,
        bool requiresPassword)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverKey);

        return requiresPassword &&
               store.TryGet(receiverKey, out var stored) &&
               !string.IsNullOrWhiteSpace(stored)
            ? stored
            : null;
    }
}
