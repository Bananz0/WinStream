namespace WinStream.Core.Persistence;

/// <summary>LAN PIN storage for WinStream Link companions — never Apple HKP material.</summary>
public interface ILinkCredentialStore
{
    bool TryGetPin(string receiverKey, out string pin);

    void SavePin(string receiverKey, string pin);

    void Remove(string receiverKey);
}
