using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Core.Persistence;

/// <summary>
/// Per-receiver HomeKit pairing identities. Abstracted so callers can supply an
/// in-memory fake instead of touching <c>%LocalAppData%</c>.
/// </summary>
public interface IPairingCredentialStore
{
    bool TryGet(string receiverKey, out PairingCredentials credentials);

    void Save(string receiverKey, PairingCredentials credentials);

    void Remove(string receiverKey);
}
