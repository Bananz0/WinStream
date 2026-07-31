namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Everything the control channel needs to prefer a stored HomeKit pairing over
/// transient setup. The caller owns persistence: the protocol only reads
/// <see cref="StoredCredentials"/> and reports outcomes back through the callbacks.
/// </summary>
public sealed class PairingOptions
{
    /// <summary>Identity from a previous pair-setup, or <c>null</c> to pair fresh.</summary>
    public PairingCredentials? StoredCredentials { get; init; }

    /// <summary>
    /// Called after the receiver shows its AirPlay code. Return the digits, or
    /// <c>null</c> to skip persistent pairing and fall back to transient.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? RequestPinAsync { get; init; }

    /// <summary>Invoked with a fresh identity so the caller can persist it.</summary>
    public Action<PairingCredentials>? OnPaired { get; init; }

    /// <summary>Invoked when <see cref="StoredCredentials"/> no longer verify.</summary>
    public Action? OnStoredCredentialsRejected { get; init; }
}
