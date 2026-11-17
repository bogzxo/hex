using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The harness's counts kept in a file, so what it saved the agents goes on adding up across Hex's restarts instead of
/// starting at nothing with each one.
/// </summary>
public sealed class HexHarnessStatsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-stats-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    [Fact]
    public void CountsComeBackInTheNextRun()
    {
        string file = Path.Combine(folder, "stats.tsv");
        var first = new HexHarnessStats(file);
        McpServer.CallingClient.Value = "claude-code 2.1.0";
        try
        {
            first.Count("hex_read", 100, 900, kept: false);
            first.Count("hex_read", 50, 400, kept: true);
            first.Count("hex_search", 30, 300, kept: false);
        }
        finally
        {
            McpServer.CallingClient.Value = null;
        }
        first.Save();

        var second = new HexHarnessStats(file);
        Assert.Equal((3L, 180L, 1600L), second.Totals());
        Assert.Equal((3L, 180L, 1600L), second.TotalsFor(["claude-code"]));
        Assert.Contains("hex_read  2  1  150  1300", second.Describe());

        // And counting on from there
        second.Count("hex_skim", 20, 200, kept: false);
        second.Save();
        Assert.Equal((4L, 200L, 1800L), new HexHarnessStats(file).Totals());
    }

    [Fact]
    public void NoFileIsThisRunOnlyAndABrokenOneIsSkipped()
    {
        var none = new HexHarnessStats();
        none.Count("hex_read", 1, 2, kept: false);
        none.Save();
        Assert.Equal((1L, 1L, 2L), none.Totals());

        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "stats.tsv");
        File.WriteAllText(file, "tool\thex_read\t2\t0\t10\t40\nnonsense\nclient\t\t1\t1\t1\t1\ntool\thex_map\tx\t0\t0\t0\n");
        Assert.Equal((2L, 10L, 40L), new HexHarnessStats(file).Totals());
    }
}
