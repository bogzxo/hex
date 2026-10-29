using Horizon.Logging;
using Horizon.Logging.Loggers;

namespace Hex.Tests;

public sealed class HexLogTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-log-test-").FullName;
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_new_launch_resets_the_single_log_without_pid_fallback_or_null_gaps()
    {
        string path = Path.Combine(folder, "debug.log");
        using var first = new ConcurrentLogger(path, singleFile: true);
        first.Log(LogLevel.Info, "old session");
        first.Dispose();
        Assert.Contains("old session", System.IO.File.ReadAllText(path));
        using var active = new ConcurrentLogger(path, singleFile: true);
        using var newest = new ConcurrentLogger(path, singleFile: true);
        active.Log(LogLevel.Info, "active window");
        newest.Log(LogLevel.Info, "new window");
        active.Dispose(); newest.Dispose();
        string log = System.IO.File.ReadAllText(path);
        Assert.DoesNotContain("old session", log);
        Assert.DoesNotContain('\0', log);
        Assert.Contains("active window", log);
        Assert.Contains("new window", log);
        Assert.Single(Directory.GetFiles(folder, "*.log"));
    }
}
