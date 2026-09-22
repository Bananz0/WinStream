using System.Text.Json;
using WinStream.Core.Persistence;

namespace WinStream.Tests;

public class LinkCredentialStoreTests
{
    [Fact]
    public void Saved_pin_round_trips_through_a_fresh_store()
    {
        using var directory = new TempDirectory();
        new LinkCredentialStore(directory.Path).SavePin("192.168.1.50:47200", "8421");

        Assert.True(new LinkCredentialStore(directory.Path)
            .TryGetPin("192.168.1.50:47200", out var pin));
        Assert.Equal("8421", pin);
    }

    [Fact]
    public void Unknown_receiver_reports_no_pin()
    {
        using var directory = new TempDirectory();
        var store = new LinkCredentialStore(directory.Path);

        Assert.False(store.TryGetPin("192.168.1.99:47200", out var pin));
        Assert.Equal(string.Empty, pin);
    }

    [Fact]
    public void Removed_receiver_stops_returning_its_pin()
    {
        using var directory = new TempDirectory();
        var store = new LinkCredentialStore(directory.Path);
        store.SavePin("192.168.1.50:47200", "8421");

        store.Remove("192.168.1.50:47200");

        Assert.False(store.TryGetPin("192.168.1.50:47200", out _));
    }

    [Fact]
    public void Pin_is_not_readable_as_plaintext_on_disk()
    {
        using var directory = new TempDirectory();
        new LinkCredentialStore(directory.Path).SavePin("192.168.1.50:47200", "8421");

        var onDisk = File.ReadAllText(Path.Combine(directory.Path, "link-credentials.json"));

        Assert.DoesNotContain("8421", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.50", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_and_upgrades_a_legacy_plaintext_file()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "link-credentials.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["192.168.1.50:47200"] = "8421"
            }));

        var store = new LinkCredentialStore(directory.Path);
        Assert.True(store.TryGetPin("192.168.1.50:47200", out var pin));
        Assert.Equal("8421", pin);

        store.SavePin("192.168.1.51:47200", "9999");
        Assert.DoesNotContain("8421", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Link_pins_never_land_in_the_Apple_pairing_file()
    {
        using var directory = new TempDirectory();
        var link = new LinkCredentialStore(directory.Path);
        var apple = new PairingCredentialStore(directory.Path);

        link.SavePin("192.168.1.9:47200", "1234");

        Assert.False(apple.TryGet("192.168.1.9:47200", out _));
        Assert.True(File.Exists(Path.Combine(directory.Path, "link-credentials.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "pairings.json")));
    }

    [Fact]
    public void Blank_arguments_are_rejected()
    {
        using var directory = new TempDirectory();
        var store = new LinkCredentialStore(directory.Path);

        Assert.Throws<ArgumentException>(() => store.SavePin("  ", "8421"));
        Assert.Throws<ArgumentException>(() => store.SavePin("192.168.1.50:47200", "  "));
        Assert.Throws<ArgumentException>(() => store.TryGetPin("  ", out _));
    }
}
