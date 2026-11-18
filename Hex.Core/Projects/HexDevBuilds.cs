#if DEBUG
namespace Hex.Core;

/// <summary>
/// The good build of a Hex working on itself, a copy of the build that was running kept beside the folder it runs from,
/// for the guard (Hex's DevGuard) to put back when a new build doesn't come up. Only in a debug build.
/// </summary>
internal static class HexDevBuilds
{
    // The folders of a build that are Hex's state rather than the build, never copied over either way
    internal static readonly string[] KEPT = ["settings", "extensions", "extension_catalog", "cache", "layouts", "backups", "data"];

    /// <summary>Where the good build of a folder Hex runs from is kept, beside it.</summary>
    public static string GoodFolder(string bin) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(bin)) + ".last-good";

    /// <summary>
    /// A build's files copied from one folder to another, those that differ, Hex's state left where it is. How many were
    /// copied. The folder a build is kept in as the good one, and put back from.
    /// </summary>
    internal static int Mirror(string from, string to)
    {
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            string top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (KEPT.Contains(top, StringComparer.OrdinalIgnoreCase) || file.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".hexold", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                continue;

            string target = Path.Combine(to, relative);
            var source = new FileInfo(file);
            var there = new FileInfo(target);
            if (there.Exists && there.Length == source.Length && there.LastWriteTimeUtc == source.LastWriteTimeUtc)
                continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
                copied++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return copied;
    }
}
#endif
