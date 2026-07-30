using WinStream.Core.Persistence;

namespace WinStream.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSelectedDevice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinStreamTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            store.Save(new AppSettings
            {
                SelectedRenderDeviceId = "endpoint-123",
                MonitorCapture = true
            });

            var loaded = store.Load();
            Assert.Equal("endpoint-123", loaded.SelectedRenderDeviceId);
            Assert.True(loaded.MonitorCapture);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
