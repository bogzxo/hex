using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// Where Hex keeps a file of its own, in the settings folder next to the exe (TerminalFiles.UserFolder, the same folder
/// as settings.hor, keys.hor and the rest of Dusk's), so every one of them is a double click away from Hex.exe instead of
/// some next to it, some in AppData and some loose next to it under names nobody could tell apart. One that was loose
/// next to the exe from before is moved in the first time it's asked for.
/// </summary>
public static class HexFiles
{
    /// <summary>The path of one of Hex's files.</summary>
    /// <param name="was">What it was called next to the exe before, when that isn't its name now.</param>
    public static string Kept(string name, string? was = null)
    {
        string path = Path.Combine(TerminalFiles.UserFolder, name);
        string old = Path.Combine(AppContext.BaseDirectory, was ?? name);
        if (!File.Exists(path) && File.Exists(old) && !string.Equals(Path.GetFullPath(old), Path.GetFullPath(path), Horizon.Core.Platform.PlatformPaths.Comparison))
        {
            try
            {
                File.Move(old, path);
            }
            catch (Exception)
            {
                // left where it was, and the new one starts out empty
            }
        }

        return path;
    }
}
