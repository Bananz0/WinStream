using System.Text.Json;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

public class PairingCredentialStoreTests
{
    private static PairingCredentials Sample(string suffix = "1") => new()
    {
        ClientPairingId = "CLIENT-" + suffix,
        ClientSeedHex = new string('A', 64),
        AccessoryPairingId = "ACCESSORY-" + suffix,
        AccessoryPublicKeyHex = new string('C', 64)
    };

    [Fact]
    public void RoundTrips_every_field_per_receiver()
    {
        using var directory = new TempDirectory();
        var credentials = Sample();
        new PairingCredentialStore(directory.Path).Save("receiver-a", credentials);

        var reloaded = new PairingCredentialStore(directory.Path);
        Assert.True(reloaded.TryGet("receiver-a", out var loaded));
        Assert.Equal(credentials.ClientPairingId, loaded.ClientPairingId);
        Assert.Equal(credentials.ClientSeedHex, loaded.ClientSeedHex);
        Assert.Equal(credentials.AccessoryPairingId, loaded.AccessoryPairingId);
        Assert.Equal(credentials.AccessoryPublicKeyHex, loaded.AccessoryPublicKeyHex);
        Assert.False(reloaded.TryGet("receiver-b", out _));
    }

    [Fact]
    public void Remove_forgets_only_the_named_receiver()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);
        store.Save("receiver-a", Sample("a"));
        store.Save("receiver-b", Sample("b"));

        store.Remove("receiver-a");

        Assert.False(store.TryGet("receiver-a", out _));
        Assert.True(store.TryGet("receiver-b", out _));
    }

    [Fact]
    public void TryGet_is_case_insensitive()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);
        store.Save("AA:BB:CC:DD:EE:FF", Sample());

        Assert.True(store.TryGet("aa:bb:cc:dd:ee:ff", out _));
    }

    [Fact]
    public void Save_rejects_incomplete_credentials()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);

        Assert.Throws<ArgumentException>(() =>
            store.Save("receiver-a", new PairingCredentials { ClientPairingId = "only-id" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_receiver_keys_throw(string key)
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.TryGet(key, out _));
        Assert.Throws<ArgumentException>(() => store.Save(key, Sample()));
        Assert.Throws<ArgumentException>(() => store.Remove(key));
    }

    [Fact]
    public void Seed_is_not_readable_as_plaintext_on_disk()
    {
        using var directory = new TempDirectory();
        new PairingCredentialStore(directory.Path).Save("receiver-a", Sample());

        var onDisk = File.ReadAllText(Path.Combine(directory.Path, "pairings.json"));

        Assert.DoesNotContain(new string('A', 64), onDisk, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CLIENT-1", onDisk, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_and_upgrades_a_legacy_plaintext_file()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "pairings.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new Dictionary<string, PairingCredentials> { ["receiver-a"] = Sample() },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var store = new PairingCredentialStore(directory.Path);
        Assert.True(store.TryGet("receiver-a", out var loaded));
        Assert.Equal("CLIENT-1", loaded.ClientPairingId);

        // Any write rewrites the whole map through the protected envelope.
        store.Save("receiver-b", Sample("b"));
        Assert.DoesNotContain("CLIENT-1", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        // The write goes to "<path>.tmp" and is moved over the real file so a crash
        // mid-write cannot truncate every pairing. A surviving temp file means the
        // move never happened.
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);

        store.Save("receiver-a", Sample("a"));
        store.Save("receiver-b", Sample("b"));

        var path = Path.Combine(directory.Path, "pairings.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(new[] { "pairings.json" }, Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void A_leftover_temp_file_from_a_crashed_write_does_not_block_the_next_save()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "pairings.json");
        File.WriteAllText(path + ".tmp", "half-written garbage");

        var store = new PairingCredentialStore(directory.Path);
        store.Save("receiver-a", Sample());

        Assert.True(store.TryGet("receiver-a", out _));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Every_save_replaces_the_whole_map_so_readers_never_see_a_partial_file()
    {
        using var directory = new TempDirectory();
        var store = new PairingCredentialStore(directory.Path);
        var path = Path.Combine(directory.Path, "pairings.json");

        for (var i = 0; i < 10; i++)
        {
            store.Save($"receiver-{i}", Sample(i.ToString()));

            // Reading between writes must always find a complete, loadable map.
            var reader = new PairingCredentialStore(directory.Path);
            Assert.True(reader.TryGet($"receiver-{i}", out _));
            Assert.False(File.Exists(path + ".corrupt"));
        }
    }

    [Fact]
    public void Corrupt_file_is_quarantined_rather_than_silently_reused()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "pairings.json");
        File.WriteAllText(path, "{ not json at all");

        var store = new PairingCredentialStore(directory.Path);

        Assert.False(store.TryGet("receiver-a", out _));
        Assert.True(File.Exists(path + ".corrupt"));
    }
}
