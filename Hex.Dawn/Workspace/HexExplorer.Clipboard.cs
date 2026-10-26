namespace Hex.Dawn;

// Files copied and cut in the explorer and pasted into a folder, VS Code's way of shuffling files about. The clipboard is
// Hex's own, paths and whether they were cut, the system's clipboard is left with whatever text was on it. A name that's
// taken comes out as "x copy.go", then "x copy 2.go", and a cut file moves with what is open of it following.
internal sealed partial class HexExplorer
{
    // What was copied or cut last, the full paths
    private readonly List<string> clipped = [];

    // Whether those were cut, a paste moves them and empties the clipboard
    private bool clippedCut;

    /// <summary>Whether there's anything to paste, something copied or cut that's still on the disk.</summary>
    internal bool CanPaste => clipped.Any(path => File.Exists(path) || Directory.Exists(path));

    /// <summary>Whether what's on the clipboard was cut, for the self test.</summary>
    internal bool ClippedCut => clippedCut && clipped.Count > 0;

    /// <summary>Files and folders put on the clipboard to be copied where they're pasted.</summary>
    internal void CopyPaths(IEnumerable<string> paths) => Clip(paths, cut: false);

    /// <summary>Files and folders put on the clipboard to be moved where they're pasted.</summary>
    internal void CutPaths(IEnumerable<string> paths) => Clip(paths, cut: true);

    private void Clip(IEnumerable<string> paths, bool cut)
    {
        clipped.Clear();
        clipped.AddRange(paths.Select(Path.GetFullPath));
        clippedCut = cut;
        if (clipped.Count > 0)
            context.Notices.Say(context.Strings.Said(cut ? "explorer_files.cut_said" : "explorer_files.copied_said", ("name", Path.GetFileName(clipped[0].TrimEnd('\\', '/'))), ("count", clipped.Count.ToString())));
    }

    /// <summary>
    /// What's on the clipboard put in a folder. Copied it's copied there, a name that's taken given " copy" on the end of
    /// its name. Cut it's moved there and the clipboard emptied, the way a cut is pasted once.
    /// </summary>
    /// <returns>Where each one ended up.</returns>
    internal List<string> PasteInto(string folder)
    {
        var made = new List<string>();
        string into = Path.GetFullPath(folder);
        foreach (string from in clipped.ToList())
        {
            if (!File.Exists(from) && !Directory.Exists(from))
                continue;

            if (clippedCut)
            {
                // Cut and pasted where it is already, nothing moves
                if (PlatformPaths.Same(Path.GetDirectoryName(from) ?? from, into))
                {
                    made.Add(from);
                    continue;
                }

                string to = FreeName(into, Path.GetFileName(from.TrimEnd('\\', '/')));
                if (MovePath(from, to))
                    made.Add(to);
                continue;
            }

            if (CopyTo(from, FreeName(into, Path.GetFileName(from.TrimEnd('\\', '/')))) is { } copy)
                made.Add(copy);
        }

        if (clippedCut)
            clipped.Clear();

        if (made.Count > 0)
        {
            workspace.Expanded.Add(into);
            treeDirty = true;
            FoldersChanged?.Invoke();
            KeepWorkspace();
            context.Notices.Say(context.Strings.Said("explorer_files.pasted", ("name", Path.GetFileName(made[0])), ("folder", Path.GetFileName(into))));
        }

        return made;
    }

    /// <summary>A file or a folder copied next to itself, "x copy.go".</summary>
    /// <returns>Where the copy is, null when it couldn't be made.</returns>
    internal string? Duplicate(string path)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full.TrimEnd('\\', '/'))!;
        if (CopyTo(full, FreeName(folder, Path.GetFileName(full.TrimEnd('\\', '/')))) is not { } copy)
            return null;

        treeDirty = true;
        FoldersChanged?.Invoke();
        context.Notices.Say(context.Strings.Said("explorer_files.made", ("path", copy)));
        return copy;
    }

    /// <summary>
    /// A name in a folder that isn't taken, the one asked for, or with " copy" on the end of its name before the extension,
    /// then " copy 2" and on. A dotfile (.env) keeps its dot in front, ".env copy".
    /// </summary>
    internal static string FreeName(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        bool dotfile = name.StartsWith('.') && name.IndexOf('.', 1) < 0;
        string stem = dotfile ? name : Path.GetFileNameWithoutExtension(name), extension = dotfile ? string.Empty : Path.GetExtension(name);
        for (int number = 1; ; number++)
        {
            string tried = Path.Combine(folder, number == 1 ? $"{stem} copy{extension}" : $"{stem} copy {number}{extension}");
            if (!File.Exists(tried) && !Directory.Exists(tried))
                return tried;
        }
    }

    // Helper method to copy a file, or a folder and everything in it, said when it can't be
    private string? CopyTo(string from, string to)
    {
        try
        {
            if (File.Exists(from))
            {
                File.Copy(from, to);
                return to;
            }

            // A folder into itself would go on copying the copy
            if (PlatformPaths.IsUnder(to, from))
            {
                context.Notices.Say(context.Strings.Said("explorer_files.into_itself", ("name", Path.GetFileName(from))), error: true);
                return null;
            }

            CopyFolder(from, to);
            return to;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Notices.Say(context.Strings.Said("explorer_files.not_copied", ("name", Path.GetFileName(from)), ("why", e.Message)), error: true);
            return null;
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string folder in Directory.EnumerateDirectories(from))
            CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
