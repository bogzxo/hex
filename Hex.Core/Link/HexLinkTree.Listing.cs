using Hex.Core.Git;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    private sealed record DiskGitLayout(string[] Repositories, ILookup<string, string>? Candidates);

    /// <summary>A path with a slash between its parts, relative to a folder (starts with .. when it isn't under it).</summary>
    internal static string RelativeTo(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// A path with every link in it resolved, the way a file watcher or git may spell it (macOS's /var is /private/var),
    /// null when it can't be told. Each folder from the top down is looked at, a link taken to where it ends up.
    /// </summary>
    internal static string? RealPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!OperatingSystem.IsWindows())
                return Canonicalise(full, static candidate => new FileInfo(candidate).LinkTarget);

            string current = Path.GetPathRoot(full) ?? string.Empty;
            foreach (string part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }

            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path with every link in it, ancestors and the targets' own ancestors included, taken to where it ends up.
    /// Walks the parts from the top, a link's target (relative ones against the folder holding the link) is walked in
    /// its place, and a part that doesn't exist (no link there) is kept as written with what follows it.
    /// Null when links loop. <paramref name="linkOf"/> gives the raw target of a link at a path, null when it isn't one.
    /// </summary>
    internal static string? Canonicalise(string full, Func<string, string?> linkOf)
    {
        const int MostLinks = 40;
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        string current = Path.GetPathRoot(full) ?? string.Empty;
        var pending = new Stack<string>();
        string[] first = full[current.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = first.Length - 1; i >= 0; i--)
            pending.Push(first[i]);
        int links = 0;
        while (pending.Count > 0)
        {
            string part = pending.Pop();
            if (part == ".")
                continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current.TrimEnd(separators)) is { } parent ? parent : current;
                continue;
            }

            string candidate = Path.Combine(current, part);
            string? target = linkOf(candidate);
            if (target is null)
            {
                current = candidate;
                continue;
            }

            if (++links > MostLinks)
                return null;
            string resolved = Path.IsPathRooted(target) ? target : Path.Combine(current, target);
            string root = Path.GetPathRoot(resolved) ?? string.Empty;
            if (root.Length == 0)
                root = current;
            else
                current = root;
            string[] parts = resolved[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
                pending.Push(parts[i]);
        }

        return current;
    }

    private string lastHeadsStamp = string.Empty;

    /// <summary>
    /// Where the root's and every nested checkout's HEAD points, read from the files (HEAD, the branch's ref, packed-refs).
    /// The watcher leaves .git folders alone, so a commit made there moves no scan version; a scan reused for a moment
    /// would carry the old heads. Cheap: a few small files for each checkout.
    /// </summary>
    private string HeadsStamp()
    {
        var stamp = new System.Text.StringBuilder();
        foreach (string repository in (string[])[string.Empty, .. nestedRepositories])
        {
            try
            {
                string directory = repository.Length == 0 ? Root : Path.Combine(Root, repository);
                string marker = Path.Combine(directory, ".git");
                string gitDir = marker;
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() is { } pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    gitDir = Path.GetFullPath(pointer[7..].Trim(), directory);
                string common = gitDir;
                string commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile))
                    common = Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir);
                string head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                stamp.Append(repository).Append('|').Append(head).Append('|');
                if (head.StartsWith("ref:", StringComparison.Ordinal))
                {
                    string reference = head[4..].Trim();
                    string file = Path.Combine(common, reference);
                    if (File.Exists(file))
                        stamp.Append(File.ReadAllText(file).Trim());
                    else if (File.Exists(Path.Combine(common, "packed-refs")))
                    {
                        var packed = new FileInfo(Path.Combine(common, "packed-refs"));
                        stamp.Append(packed.Length).Append('@').Append(packed.LastWriteTimeUtc.Ticks);
                    }
                }
                stamp.Append('\n');
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable: a stamp that never repeats, so the scan is made again
                return Guid.NewGuid().ToString("N");
            }
        }

        return stamp.ToString();
    }

    private long reusedGitListings;
    internal long ReusedGitListings => Interlocked.Read(ref reusedGitListings);

    private DiskGitLayout DiscoverGitLayout(Dictionary<string, HexLinkGitIssue> issues, bool allowIndexReuse, Action<string>? omittedSystemFile)
    {
        List<string>? inventory = allowIndexReuse && !trackedIndexes.IsEmpty && PreviousManifest is not null ? [] : null;
        string[] repositories = [.. FindNestedRepositories(Root, unavailable: (path, why) =>
            issues[path] = new HexLinkGitIssue(OneLine(why), Unavailable: true), inventory: inventory, includeSystemFiles: Folder.IncludeSystemFiles,
                omittedSystemFile: omittedSystemFile)
            .Where(path => !Hidden(path, folder: true)).OrderByDescending(path => path.Length)];
        ILookup<string, string>? candidates = inventory is { Count: <= MOST_FILES } && issues.Count == 0
            ? inventory.ToLookup(path => RepositoryContaining(path, repositories) ?? string.Empty, StringComparer.Ordinal)
            : null;
        return new DiskGitLayout(repositories, candidates);
    }

    // Only a fully tracked scope can reuse names without re-evaluating Git ignore rules. The disk inventory exposes
    // any untracked candidate even if it appeared while Hex was closed, or the directory timestamp was restored.
    private GitResult ListGitWorkingFiles(string directory, string scope, DiskGitLayout layout, TimeSpan patience,
        out GitResult? headAlreadyRead)
    {
        headAlreadyRead = null;
        HexLinkManifest? prior = PreviousManifest;
        string? previousHead = scope.Length == 0 ? prior?.Head : prior?.NestedHeads?.GetValueOrDefault(scope);
        if (previousHead is not null && prior?.GitIssues?.ContainsKey(scope) != true &&
            layout.Candidates is { } candidates && TrackedNow(directory, cachedOnly: true) is { } tracked)
        {
            int prefix = scope.Length == 0 ? 0 : scope.Length + 1;
            if (candidates[scope].All(path => path.Length > prefix && tracked.Contains(path[prefix..])))
            {
                GitResult head = GitProcess.Run(directory, ["rev-parse", "--verify", "--quiet", "HEAD"], patience: patience);
                headAlreadyRead = head;
                if (head.Ok && head.Output.Trim() == previousHead)
                {
                    using var timing = HexLinkSyncTiming.Measure("tree.list-exact-index", tracked.Count);
                    Interlocked.Increment(ref reusedGitListings);
                    return new GitResult(0, string.Join('\0', tracked), string.Empty);
                }
            }
        }

        using var listing = HexLinkSyncTiming.Measure(scope.Length == 0 ? "tree.list-root-git" : "tree.list-nested-git");
        return GitProcess.Run(directory, ["ls-files", "-co", "--exclude-standard", "-z"], patience: patience);
    }
}
