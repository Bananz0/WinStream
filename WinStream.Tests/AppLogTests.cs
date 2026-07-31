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
    public void File_sink_persists_lines_and_redacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winstream-log-{Guid.NewGuid():N}");
        try
        {
            var path = AppLog.EnableFileSink(directory);
            Assert.NotNull(path);
            Assert.Equal(path, AppLog.FilePath);

            AppLog.Error("test", "disk-persisted-line");
            AppLog.Warn("test", "aesiv=secret");

            var contents = File.ReadAllText(path!);
            Assert.Contains("disk-persisted-line", contents);
            Assert.Contains("[redacted]", contents);
            Assert.DoesNotContain("aesiv=secret", contents);
        }
        finally
        {
            AppLog.DisableFileSink();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sanitize_redacts_key_material()
    {
        AppLog.Warn("test", "rsaaeskey=abc");
        var lines = AppLog.Snapshot();
        Assert.Contains(lines, line => line.Contains("[redacted]"));
        Assert.DoesNotContain(lines, line => line.Contains("rsaaeskey=abc"));
    }

    [Fact]
    public void Sanitize_redacts_shk_material()
    {
        AppLog.Warn("test", "shk=deadbeef");
        var lines = AppLog.Snapshot();
        Assert.Contains(lines, line => line.Contains("[redacted]"));
        Assert.DoesNotContain(lines, line => line.Contains("shk=deadbeef"));
    }
}
