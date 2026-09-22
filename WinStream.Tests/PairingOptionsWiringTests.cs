using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Covers <see cref="PairingOptionsFactory"/> — the same builder the orchestrator
/// calls — so clear-before-attempt and store callbacks cannot silently drift.
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
        Func<CancellationToken, Task<string?>>? requestPin = null,
        FakeSessionMap? sessions = null)
    {
        // Mirrors StreamingOrchestrator.CreatePairingOptions: the caller clears any
        // stale transient mark before building options, not the factory itself.
        sessions?.ClearTransient(receiverKey);
        return PairingOptionsFactory.Create(
            store,
            receiverKey,
            requestPin,
            markTransient: () => sessions?.MarkTransient(receiverKey));
    }


    [Fact]
    public void OnTransientPairing_marks_only_the_live_session_for_that_receiver()
    {
        var store = new FakePairingCredentialStore();
        var sessions = new FakeSessionMap();
        sessions.Add("receiver-a");
        sessions.Add("receiver-b");

        Build(store, "receiver-a", sessions: sessions).OnTransientPairing!();

        Assert.True(sessions.UsesTransientPairing("receiver-a"));
        Assert.False(sessions.UsesTransientPairing("receiver-b"));
    }

    [Fact]
    public void Removing_the_session_drops_its_transient_mark()
    {
        var store = new FakePairingCredentialStore();
        var sessions = new FakeSessionMap();
        sessions.Add("receiver-a");
        Build(store, "receiver-a", sessions: sessions).OnTransientPairing!();

        sessions.Remove("receiver-a");

        Assert.False(sessions.UsesTransientPairing("receiver-a"));
    }

    [Fact]
    public void A_reconnect_that_trusts_the_PC_no_longer_reports_transient()
    {
        var store = new FakePairingCredentialStore();
        var sessions = new FakeSessionMap();
        sessions.Add("receiver-a");
        Build(store, "receiver-a", sessions: sessions).OnTransientPairing!();

        // Fresh attempt on the same SessionEntry (quality rebuild): clear-before-attempt
        // drops the temporary mark, and pair-verify never fires OnTransientPairing.
        Build(store, "receiver-a", sessions: sessions);

        Assert.False(sessions.UsesTransientPairing("receiver-a"));
    }

    [Fact]
    public void A_transient_report_without_a_live_session_marks_nothing()
    {
        var store = new FakePairingCredentialStore();
        var sessions = new FakeSessionMap();

        Build(store, "receiver-a", sessions: sessions).OnTransientPairing!();

        Assert.False(sessions.UsesTransientPairing("receiver-a"));
    }

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
    public void Options_carry_the_pin_callback_unchanged()
    {
        var store = new FakePairingCredentialStore();
        Func<CancellationToken, Task<string?>> pin = _ => Task.FromResult<string?>("1234");

        var options = Build(store, "receiver-a", requestPin: pin);

        Assert.Same(pin, options.RequestPinAsync);
    }

    [Fact]
    public void A_password_path_keeps_identity_and_pin_callbacks()
    {
        var store = new FakePairingCredentialStore();
        store.Save("receiver-a", Complete());
        Func<CancellationToken, Task<string?>> pin = _ => Task.FromResult<string?>("1234");
        var sessions = new FakeSessionMap();
        sessions.Add("receiver-a");

        sessions.ClearTransient("receiver-a");
        var options = PairingOptionsFactory.Create(
            store,
            "receiver-a",
            pin,
            markTransient: () => sessions.MarkTransient("receiver-a"),
            receiverPassword: "hunter2");

        Assert.Equal("hunter2", options.ReceiverPassword);
        Assert.NotNull(options.StoredCredentials);
        Assert.Equal("CLIENT", options.StoredCredentials!.ClientPairingId);
        Assert.Same(pin, options.RequestPinAsync);
        Assert.NotNull(options.OnPaired);
        Assert.NotNull(options.OnStoredCredentialsRejected);
        Assert.NotNull(options.OnTransientPairing);
        Assert.False(sessions.UsesTransientPairing("receiver-a"));
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

    /// <summary>
    /// Mirrors the orchestrator's session map: the transient flag lives on the session
    /// entry, so it appears when the callback fires and disappears with the session.
    /// </summary>
    private sealed class FakeSessionMap
    {
        private readonly Dictionary<string, bool> _sessions = new(StringComparer.Ordinal);

        public void Add(string receiverKey) => _sessions[receiverKey] = false;

        public void Remove(string receiverKey) => _sessions.Remove(receiverKey);

        public void ClearTransient(string receiverKey)
        {
            if (_sessions.ContainsKey(receiverKey))
            {
                _sessions[receiverKey] = false;
            }
        }

        public void MarkTransient(string receiverKey)
        {
            if (_sessions.ContainsKey(receiverKey))
            {
                _sessions[receiverKey] = true;
            }
        }

        public bool UsesTransientPairing(string receiverKey) =>
            _sessions.TryGetValue(receiverKey, out var transient) && transient;
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
