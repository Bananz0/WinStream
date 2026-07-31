using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// The orchestrator builds <see cref="PairingOptions"/> from a store; these cover the
/// same shape without WinUI so the callbacks cannot silently stop reaching the store.
/// </summary>
public class PairingOptionsWiringTests
{
    private static PairingCredentials Complete(string id = "CLIENT") => new()
    {
        ClientPairingId = id,
        ClientSeedHex = new string('A', 64),
        AccessoryPairingId = "ACCESSORY",
        AccessoryPublicKeyHex = new string('C', 64)
    };

    private static PairingOptions Build(
        IPairingCredentialStore store,
        string receiverKey,
        Func<CancellationToken, Task<string?>>? requestPin = null) => new()
    {
        StoredCredentials = store.TryGet(receiverKey, out var stored) ? stored : null,
        RequestPinAsync = requestPin,
        OnPaired = credentials => store.Save(receiverKey, credentials),
        OnStoredCredentialsRejected = () => store.Remove(receiverKey)
    };

    [Fact]
    public void Options_carry_the_stored_identity_for_a_known_receiver()
    {
        var store = new FakePairingCredentialStore();
        store.Save("receiver-a", Complete());

        var options = Build(store, "receiver-a");

        Assert.NotNull(options.StoredCredentials);
        Assert.Equal("CLIENT", options.StoredCredentials!.ClientPairingId);
    }

    [Fact]
    public void Options_carry_no_identity_for_an_unknown_receiver()
    {
        var store = new FakePairingCredentialStore();

        Assert.Null(Build(store, "receiver-a").StoredCredentials);
    }

    [Fact]
    public void OnPaired_writes_through_to_the_store()
    {
        var store = new FakePairingCredentialStore();

        Build(store, "receiver-a").OnPaired!(Complete("FRESH"));

        Assert.True(store.TryGet("receiver-a", out var saved));
        Assert.Equal("FRESH", saved.ClientPairingId);
    }

    [Fact]
    public void OnStoredCredentialsRejected_clears_only_that_receiver()
    {
        var store = new FakePairingCredentialStore();
        store.Save("receiver-a", Complete("A"));
        store.Save("receiver-b", Complete("B"));

        Build(store, "receiver-a").OnStoredCredentialsRejected!();

        Assert.False(store.TryGet("receiver-a", out _));
        Assert.True(store.TryGet("receiver-b", out _));
    }

    [Fact]
    public async Task A_session_accepts_pairing_options_and_keys_off_the_same_receiver()
    {
        var receiver = new DeviceInfo
        {
            DisplayName = "Mac",
            Model = "Mac",
            IPAddress = "192.168.1.50",
            Port = 7000
        };
        var store = new FakePairingCredentialStore();
        var receiverKey = ReceiverKey.For(receiver);
        store.Save(receiverKey, Complete());

        await using var session = new AirPlay2Session(
            receiver,
            senderDeviceId: "AA:BB:CC:DD:EE:01",
            pairingOptions: Build(store, receiverKey));

        Assert.Equal(receiverKey, session.ReceiverId);
        Assert.Equal(SessionState.Disconnected, session.State);
    }

    private sealed class FakePairingCredentialStore : IPairingCredentialStore
    {
        private readonly Dictionary<string, PairingCredentials> _map =
            new(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string receiverKey, out PairingCredentials credentials)
        {
            if (_map.TryGetValue(receiverKey, out var found))
            {
                credentials = found;
                return true;
            }

            credentials = new PairingCredentials();
            return false;
        }

        public void Save(string receiverKey, PairingCredentials credentials) =>
            _map[receiverKey] = credentials;

        public void Remove(string receiverKey) => _map.Remove(receiverKey);
    }
}
