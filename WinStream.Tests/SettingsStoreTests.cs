using WinStream.Core.Persistence;

namespace WinStream.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSelectedDevice()
    {
        using var directory = new TempDirectory();
        var store = new SettingsStore(directory.Path);
        store.Save(new AppSettings
        {
            SelectedRenderDeviceId = "endpoint-123",
            MonitorCapture = true,
            AutoConnectLastReceiver = true,
            LastReceiverKey = "AA:BB:CC:DD:EE:FF",
            LastReceiverName = "Living Room",
            PreferVirtualDriver = true
        });

        var loaded = store.Load();

        Assert.Equal("endpoint-123", loaded.SelectedRenderDeviceId);
        Assert.True(loaded.MonitorCapture);
        Assert.True(loaded.AutoConnectLastReceiver);
        Assert.Equal("AA:BB:CC:DD:EE:FF", loaded.LastReceiverKey);
        Assert.Equal("Living Room", loaded.LastReceiverName);
        Assert.True(loaded.PreferVirtualDriver);
    }

    [Fact]
    public void Load_without_a_settings_file_returns_safe_defaults()
    {
        using var directory = new TempDirectory();

        var loaded = new SettingsStore(directory.Path).Load();

        Assert.False(loaded.AutoConnectLastReceiver);
        Assert.Null(loaded.LastReceiverKey);
        Assert.Null(loaded.LastReceiverName);
        Assert.False(loaded.MonitorCapture);
        Assert.Equal(CaptureMode.Loopback, loaded.CaptureMode);
        Assert.False(loaded.PreferVirtualDriver);
    }

    [Fact]
    public void Load_with_corrupt_json_returns_safe_defaults()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{ not json");

        var loaded = new SettingsStore(directory.Path).Load();

        Assert.False(loaded.AutoConnectLastReceiver);
        Assert.Null(loaded.LastReceiverKey);
        Assert.Null(loaded.SenderDeviceId);
    }

    [Fact]
    public void Update_reloads_before_saving_so_concurrent_writers_do_not_clobber()
    {
        using var directory = new TempDirectory();
        var service = new AppSettingsService(new SettingsStore(directory.Path));

        // A second writer persists a field while the first holds a stale snapshot.
        var senderId = service.EnsureSenderDeviceId();
        new SettingsStore(directory.Path).Save(new AppSettings
        {
            SenderDeviceId = senderId,
            SelectedRenderDeviceId = "written-elsewhere"
        });

        service.Update(settings => settings.LastReceiverKey = "kitchen");

        var loaded = new SettingsStore(directory.Path).Load();
        Assert.Equal("kitchen", loaded.LastReceiverKey);
        Assert.Equal("written-elsewhere", loaded.SelectedRenderDeviceId);
        Assert.Equal(senderId, loaded.SenderDeviceId);
    }

    [Fact]
    public void EnsureSenderDeviceId_persists_one_id_across_services()
    {
        using var directory = new TempDirectory();

        var first = new AppSettingsService(new SettingsStore(directory.Path)).EnsureSenderDeviceId();
        var second = new AppSettingsService(new SettingsStore(directory.Path)).EnsureSenderDeviceId();

        Assert.Equal(first, second);
        Assert.True(SenderIdentity.LooksLikeMac(first));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinStreamTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
