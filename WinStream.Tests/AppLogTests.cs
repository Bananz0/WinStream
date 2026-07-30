using WinStream.Core.Logging;

namespace WinStream.Tests;

public class AppLogTests
{
    [Fact]
    public void Info_writes_snapshot_line()
    {
        AppLog.Info("test", "hello-world");
        var lines = AppLog.Snapshot();
        Assert.Contains(lines, line => line.Contains("INFO") && line.Contains("hello-world"));
    }

    [Fact]
    public void Sanitize_redacts_key_material()
    {
        AppLog.Warn("test", "rsaaeskey=abc");
        var lines = AppLog.Snapshot();
        Assert.Contains(lines, line => line.Contains("[redacted]"));
        Assert.DoesNotContain(lines, line => line.Contains("rsaaeskey=abc"));
    }
}
