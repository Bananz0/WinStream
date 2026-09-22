using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Forget pairing is the user-facing reset for a single receiver: drop the stored
/// HomeKit identity so the next connect re-prompts for code or password.
/// </summary>
public class ForgetPairingTests
{
    private static PairingCredentials Sample() => new()
    {
        ClientPairingId = "CLIENT-1",
        ClientSeedHex = new string('A', 64),
        AccessoryPairingId = "ACCESSORY-1",
        AccessoryPublicKeyHex = new string('C', 64)
    };

    [Fact]
    public void HasStored_follows_the_credential_store()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);

        Assert.False(PairingForget.HasStored(store, "receiver-a"));

        store.Save("receiver-a", Sample());

        Assert.True(PairingForget.HasStored(store, "receiver-a"));
    }

    [Fact]
    public void Forget_removes_only_that_receiver()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);
        store.Save("receiver-a", Sample());
        store.Save("receiver-b", Sample());

        Assert.True(PairingForget.Forget(store, "receiver-a"));

        Assert.False(PairingForget.HasStored(store, "receiver-a"));
        Assert.True(PairingForget.HasStored(store, "receiver-b"));
    }

    [Fact]
    public void Forget_is_idempotent_when_nothing_was_stored()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);

        Assert.False(PairingForget.Forget(store, "receiver-a"));
        Assert.False(PairingForget.HasStored(store, "receiver-a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_keys_throw(string key)
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);

        Assert.Throws<ArgumentException>(() => PairingForget.HasStored(store, key));
        Assert.Throws<ArgumentException>(() => PairingForget.Forget(store, key));
    }

    [Fact]
    public void Forget_copy_explains_the_next_connect_will_re_prompt()
    {
        Assert.Equal("Forget pairing", PairingCopy.ForgetButton);
        Assert.Contains("AirPlay code or password", PairingCopy.ForgetDoneBody);
        Assert.Contains("No saved pairing", PairingCopy.ForgetNothingBody);
    }
}
