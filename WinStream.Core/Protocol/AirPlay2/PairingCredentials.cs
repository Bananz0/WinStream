namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Long-term HomeKit pairing identity exchanged during persistent pair-setup.
/// The seed is an Ed25519 private key — the store protects it at rest.
/// </summary>
public sealed class PairingCredentials
{
    public string ClientPairingId { get; init; } = string.Empty;

    public string ClientSeedHex { get; init; } = string.Empty;

    public string AccessoryPairingId { get; init; } = string.Empty;

    public string AccessoryPublicKeyHex { get; init; } = string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ClientPairingId) &&
        !string.IsNullOrWhiteSpace(ClientSeedHex) &&
        !string.IsNullOrWhiteSpace(AccessoryPairingId) &&
        !string.IsNullOrWhiteSpace(AccessoryPublicKeyHex);
}
