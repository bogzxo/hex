using System.Diagnostics;

using Hex.Core;

namespace Hex.Tests;

public class ZzSettingsTimingTests
{
    [Fact]
    public void Timing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zz-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "settings.hor");
        var s = new HexSettings(file);
        s.Save();
        var sw = new Stopwatch();
        var lines = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            s.Minimap = !s.Minimap;
            sw.Restart();
            s.Save();
            lines.Add($"save {sw.Elapsed.TotalMilliseconds:F2} ms");
        }
        sw.Restart();
        _ = s.EditorValues();
        lines.Add($"EditorValues {sw.Elapsed.TotalMilliseconds:F2} ms");
        sw.Restart();
        _ = Horizon.UI.Components.SettingsSections.Read(file);
        lines.Add($"Read {sw.Elapsed.TotalMilliseconds:F2} ms");
        File.WriteAllLines(@"C:/Users/bogdan/source/repos/bogzxo/horizon/hex/Hex/bin/Debug/net11.0/data/agents/temp/scratch/zz-timing.txt", lines);
    }
}
