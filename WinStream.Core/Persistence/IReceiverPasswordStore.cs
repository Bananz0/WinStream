namespace WinStream.Core.Persistence;

/// <summary>
/// Per-receiver AirPlay Receiver passwords (the System Settings password),
/// not HomeKit pairing identities and not WinStream Link PINs.
/// </summary>
public interface IReceiverPasswordStore
{
    bool TryGet(string receiverKey, out string password);

    void Save(string receiverKey, string password);

    void Remove(string receiverKey);
}
