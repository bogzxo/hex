using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The layouts that were worked on last, kept in a file in the settings folder next to the editor between runs.
internal sealed partial class HexScene
{
    // Where it keeps the layouts that were worked on last (one path a line, the latest first), and how many of them
    private const string RECENT_FILE = "hex_recent.txt";
    private const int MAX_RECENT = 8;

    /* The layouts that were worked on last */

    private static string RecentPath => HexFiles.Kept(RECENT_FILE);

    // The editor testing itself or checking layouts starts out the same every time, and leaves nothing behind
    private bool KeepsRecent => !options.SelfTest && options.Check.Length == 0;

    private List<string> ReadRecent()
    {
        if (!KeepsRecent || !File.Exists(RecentPath))
            return [];

        try
        {
            // Only the ones that are still there
            return [.. File.ReadAllLines(RecentPath).Where(line => line.Length > 0 && File.Exists(line)).Distinct().Take(MAX_RECENT)];
        }
        catch (Exception)
        {
            // Not knowing what was open last time is no reason not to start
            return [];
        }
    }

    /// <summary>Helper to note a layout as the one that was worked on last, which puts it at the top of the list.</summary>
    private void Remember(string path)
    {
        // Quick open's lead keeps every file, testing itself too, only in memory then
        RememberOpened(path);

        if (!KeepsRecent || SameDirectory(path, EDITOR_LAYOUT))
            return;

        string full = Path.GetFullPath(path);
        recent.RemoveAll(known => string.Equals(known, full, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, full);

        if (recent.Count > MAX_RECENT)
            recent.RemoveRange(MAX_RECENT, recent.Count - MAX_RECENT);

        WriteRecent();
    }

    private void ForgetRecent()
    {
        recent.Clear();
        WriteRecent();
    }

    private void WriteRecent()
    {
        RebuildFileMenu();

        try
        {
            File.WriteAllLines(RecentPath, recent);
        }
        catch (Exception e)
        {
            Say($"couldn't keep the list of layouts for next time: {e.Message}", error: true);
        }
    }
}
