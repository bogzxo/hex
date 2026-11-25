namespace Hex.Core;

/// <summary>What one pass of the empty-directory sync does, each a path relative to the folder with '/'.</summary>
internal sealed record HexLinkDirPlan(IReadOnlyList<string> MakeThere, IReadOnlyList<string> MakeHere,
    IReadOnlyList<string> RemoveThere, IReadOnlyList<string> RemoveHere, IReadOnlyList<string> Agreed)
{
    public bool Any => MakeThere.Count + MakeHere.Count + RemoveThere.Count + RemoveHere.Count > 0;
}

/// <summary>
/// Empty directories of a shared folder, which have no file in them for the file sync to carry: the empty ones listed,
/// what to do about the two sides' lists, and making and removing them inside the folder's jail, honouring the ignore rules.
/// </summary>
internal static class HexLinkDirs
{
    /// <summary>The most empty directories one side lists.</summary>
    public const int MOST = 5000;

    private const int MOST_VISITED = 20_000;

    /// <summary>
    /// The directories of a tree with no file anywhere under them, by their path from its root with '/', that sync sees:
    /// links, ignored ones (Link's rules, Git's, the workspace's) and operating system metadata aren't, and a directory with
    /// any of those inside it isn't empty.
    /// </summary>
    public static List<string> Empty(HexLinkTree tree, CancellationToken cancel = default)
    {
        var content = new Dictionary<string, bool>(StringComparer.Ordinal) { [string.Empty] = false };
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var level = new List<string> { string.Empty };
        while (level.Count > 0 && content.Count < MOST_VISITED)
        {
            cancel.ThrowIfCancellationRequested();
            var found = new List<(string Parent, string Path)>();
            foreach (string at in level)
            {
                string full = at.Length == 0 ? tree.Root : Path.Combine(tree.Root, at.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    foreach (FileSystemInfo entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry is not DirectoryInfo)
                            content[at] = true;
                        else
                            found.Add((at, at.Length == 0 ? entry.Name : at + "/" + entry.Name));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // What can't be read can't be said empty
                    content[at] = true;
                }
            }

            level = [];
            if (found.Count == 0)
                break;
            HashSet<string> ignored = tree.IgnoredKept([.. found.Select(each => each.Path + "/x")]);
            foreach (var (at, path) in found)
            {
                if (HexLinkSystemFiles.Matches(path) || ignored.Contains(path + "/x"))
                {
                    content[at] = true;
                    continue;
                }

                content[path] = false;
                parent[path] = at;
                level.Add(path);
            }
        }

        // What's under a directory counts towards it, deepest first
        foreach (string path in content.Keys.Where(path => path.Length > 0).OrderByDescending(path => path.Count(c => c == '/')).ToArray())
        {
            if (content[path] && parent.TryGetValue(path, out string? above))
                content[above] = true;
        }

        return [.. content.Where(each => each.Key.Length > 0 && !each.Value).Select(each => each.Key).Order(StringComparer.Ordinal).Take(MOST)];
    }

    /// <summary>
    /// What to do about two sides' empty directories. <paramref name="agreed"/> is what both had empty last time and
    /// <paramref name="vacated"/> the directories files were taken out of, which only ever look new when they're left over.
    /// A directory only one side lists is new there and made on the other, unless it was agreed or vacated: then the other
    /// side's lacking it is a delete, taken here (or sent there) and not made again. A directory the other side has with
    /// files in it (<paramref name="existsThere"/>, or <paramref name="existsHere"/>) is left to the file sync.
    /// </summary>
    public static HexLinkDirPlan Plan(IReadOnlyCollection<string> here, IReadOnlyCollection<string> there, IReadOnlyCollection<string> agreed,
        IReadOnlyCollection<string> vacated, Func<string, bool> existsHere, Func<string, bool> existsThere, bool canSend, bool canReceive)
    {
        var mine = new HashSet<string>(here, StringComparer.Ordinal);
        var theirs = new HashSet<string>(there, StringComparer.Ordinal);
        var was = new HashSet<string>(agreed, StringComparer.Ordinal);
        var left = new HashSet<string>(vacated, StringComparer.Ordinal);
        var makeThere = new List<string>();
        var makeHere = new List<string>();
        var removeThere = new List<string>();
        var removeHere = new List<string>();
        var now = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in mine)
        {
            if (theirs.Contains(path))
            {
                now.Add(path);
            }
            else if (existsThere(path))
            {
                // Files there, the file sync brings them here
            }
            else if (was.Contains(path) || left.Contains(path))
            {
                if (canReceive)
                    removeHere.Add(path);
                else
                    now.Add(path);
            }
            else if (canSend)
            {
                makeThere.Add(path);
                now.Add(path);
            }
        }

        foreach (string path in theirs)
        {
            if (mine.Contains(path))
                continue;
            if (existsHere(path))
            {
                // Files here, the file sync takes them over there
            }
            else if (was.Contains(path) || left.Contains(path))
            {
                if (canSend)
                    removeThere.Add(path);
                else
                    now.Add(path);
            }
            else if (canReceive)
            {
                makeHere.Add(path);
                now.Add(path);
            }
        }

        // Deepest first, so a directory is emptied of its empty directories before it goes
        static int Depth(string path) => path.Count(c => c == '/');
        return new HexLinkDirPlan([.. makeThere.OrderBy(Depth).ThenBy(path => path, StringComparer.Ordinal)],
            [.. makeHere.OrderBy(Depth).ThenBy(path => path, StringComparer.Ordinal)],
            [.. removeThere.OrderByDescending(Depth).ThenBy(path => path, StringComparer.Ordinal)],
            [.. removeHere.OrderByDescending(Depth).ThenBy(path => path, StringComparer.Ordinal)],
            [.. now.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// A directory of a folder made, with the directories above it, when it isn't one sync leaves out (ignored, operating
    /// system metadata, git's own) and stays inside the folder. False when it's refused.
    /// </summary>
    public static bool Make(HexLinkFolder folder, HexLinkTree tree, string relative, Func<HexExcludes>? excludes = null)
    {
        if (!Usable(folder, tree, relative, excludes, out string full))
            return false;
        try
        {
            Directory.CreateDirectory(full);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A directory removed when nothing at all is in it. True when it's gone, false when it isn't (or can't be).</summary>
    public static bool Remove(HexLinkFolder folder, HexLinkTree tree, string relative, Func<HexExcludes>? excludes = null)
    {
        if (!Usable(folder, tree, relative, excludes, out string full))
            return false;
        try
        {
            if (!Directory.Exists(full))
                return true;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(full).Any())
                return false;
            Directory.Delete(full);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether a directory exists here, as a directory of the tree.</summary>
    public static bool Exists(HexLinkFolder folder, string relative) =>
        Directory.Exists(Path.Combine(Path.GetFullPath(folder.Path), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Usable(HexLinkFolder folder, HexLinkTree tree, string relative, Func<HexExcludes>? excludes, out string full)
    {
        full = string.Empty;
        if (relative.Length == 0 || relative.StartsWith('/') || relative.EndsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".."))
            return false;
        try
        {
            full = HexLinkFiles.Resolve([folder], folder.Name + "/" + relative, excludes?.Invoke(), respectLinkIgnore: true);
        }
        catch (HexLinkException)
        {
            return false;
        }

        return !HexLinkSystemFiles.Matches(relative) && tree.Ignored([relative + "/x"]).Count == 0;
    }
}
