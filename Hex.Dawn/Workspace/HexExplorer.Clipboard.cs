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
    internal bool CanPaste => !transferring && clipped.Any(path => File.Exists(path) || Directory.Exists(path));
    private bool transferring;
    private int clipVersion;
    internal Action<IReadOnlyList<string>>? PastedPaths { get; set; }

    /// <summary>Whether what's on the clipboard was cut, for the self test.</summary>
    internal bool ClippedCut => clippedCut && clipped.Count > 0;
    internal bool IsCutPath(string path) => clippedCut && clipped.Any(from => PlatformPaths.Same(path, from) || PlatformPaths.IsUnder(path, from));
    internal void CancelClip() { clipped.Clear(); clippedCut = false; clipVersion++; treeDirty = true; }

    /// <summary>Files and folders put on the clipboard to be copied where they're pasted.</summary>
    internal void CopyPaths(IEnumerable<string> paths) => Clip(paths, cut: false);

    /// <summary>Files and folders put on the clipboard to be moved where they're pasted.</summary>
    internal void CutPaths(IEnumerable<string> paths) => Clip(paths, cut: true);

    private void Clip(IEnumerable<string> paths, bool cut)
    {
        clipped.Clear();
        clipVersion++;
        foreach (string path in paths.Select(Path.GetFullPath).Distinct(PlatformPaths.Comparer).OrderBy(path => path.Length))
            if (!workspace.Folders.Contains(path, PlatformPaths.Comparer) && !clipped.Any(parent => PlatformPaths.IsUnder(path, parent)))
                clipped.Add(path);
        clippedCut = cut;
        treeDirty = true;
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
        string into = Path.GetFullPath(folder);
        return FinishTransfer(Transfer(clipped.ToArray(), into, clippedCut), into, clippedCut, clipVersion);
    }

    internal void PasteIntoAsync(string folder)
    {
        if (!CanPaste) return;
        string into = Path.GetFullPath(folder);
        string[] sources = clipped.ToArray();
        bool cut = clippedCut;
        int version = clipVersion;
        transferring = true;
        context.Work.InBackground("explorer-transfer", () => Transfer(sources, into, cut), results =>
        {
            transferring = false;
            FinishTransfer(results, into, cut, version);
        });
    }

    internal sealed record TransferResult(string From, string? To, string? Error);

    internal static List<TransferResult> Transfer(IReadOnlyList<string> sources, string into, bool cut)
    {
        var results = new List<TransferResult>();
        foreach (string from in sources)
        {
            try
            {
                if (!Directory.Exists(into)) throw new DirectoryNotFoundException(into);
                if (cut && PlatformPaths.Same(Path.GetDirectoryName(from) ?? from, into))
                {
                    results.Add(new(from, from, null));
                    continue;
                }
                if (Directory.Exists(from) && (PlatformPaths.Same(into, from) || PlatformPaths.IsUnder(into, from)))
                    throw new IOException("A folder cannot be transferred into itself.");
                string to = FreeName(into, Path.GetFileName(from.TrimEnd('\\', '/')));
                if (cut)
                {
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to);
                }
                else CopyOnDisk(from, to);
                results.Add(new(from, to, null));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { results.Add(new(from, null, error.Message)); }
        }
        return results;
    }

    private List<string> FinishTransfer(List<TransferResult> results, string into, bool cut, int version)
    {
        var made = new List<string>();
        foreach (var result in results)
        {
            if (result.To is not { } to)
            {
                context.Notices.Say(context.Strings.Said("explorer_files.not_copied", ("name", Path.GetFileName(result.From)), ("why", result.Error ?? string.Empty)), error: true);
                continue;
            }
            made.Add(to);
            if (cut)
            {
                if (!PlatformPaths.Same(result.From, to)) Relocated(result.From, to);
                if (version == clipVersion) clipped.Remove(result.From);
            }
        }
        if (made.Count > 0)
        {
            workspace.Expanded.Add(into);
            tabsDirty = treeDirty = true;
            window.ShowName();
            FoldersChanged?.Invoke();
            PastedPaths?.Invoke(made);
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
            // A folder into itself would go on copying the copy
            if (PlatformPaths.IsUnder(to, from))
            {
                context.Notices.Say(context.Strings.Said("explorer_files.into_itself", ("name", Path.GetFileName(from))), error: true);
                return null;
            }

            CopyOnDisk(from, to);
            return to;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Notices.Say(context.Strings.Said("explorer_files.not_copied", ("name", Path.GetFileName(from)), ("why", e.Message)), error: true);
            return null;
        }
    }

    private static void CopyOnDisk(string from, string to)
    {
        if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recursive copies do not follow symbolic links.");
        if (File.Exists(from)) File.Copy(from, to);
        else CopyFolder(from, to);
    }

    private static void CopyFolder(string from, string to)
    {
        if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recursive copies do not follow directory links.");
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recursive copies do not follow file links.");
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (string folder in Directory.EnumerateDirectories(from))
            CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
    }
}
