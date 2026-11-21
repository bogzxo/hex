using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// A file of a workspace folder's tree as a manifest lists it: its wire path, the hash of its bytes (null when it's too
/// big to hash), its size, and for text with one kind of line end (or none) the hash of its text with CR LF made LF
/// (<see cref="Text"/>) and which kind it has (<see cref="Eol"/>, "lf", "crlf" or "none"), so two sides can tell a
/// CRLF checkout from an LF one of the same file without reading either. Both null for binary, mixed or a peer that
/// doesn't say them.
/// </summary>
internal sealed record HexLinkTreeFile(string Path, string? Hash, long Size, string? Text = null, string? Eol = null)
{
    /// <summary>Past what a link reads, listed without its hash.</summary>
    public bool Big => Hash is null;
}

/// <summary>
/// A workspace folder's tree, the commit its repository is at (null for none), every file git would carry, and the
/// repository's first commit (<see cref="Root"/>, null outside one or before its first commit), which is what tells two
/// clones of one repository from two repositories that only share a folder name.
/// </summary>
internal sealed record HexLinkManifest(string Folder, string? Head, IReadOnlyList<HexLinkTreeFile> Files, string? Root = null);

/// <summary>
/// A file of a watched tree that changed, its new hash (null with <see cref="Deleted"/> when it went, or when it's too
/// big), and its text's hash and line ends as <see cref="HexLinkTreeFile"/> has them.
/// </summary>
internal sealed record HexLinkTreeChange(string Path, string? Hash, bool Deleted = false, string? Text = null, string? Eol = null);

/// <summary>
/// A workspace folder's tree as the working-tree sync sees it: every file git would carry (tracked, and untracked but
/// not ignored, so never bin or obj), hashed. In a folder that isn't a repository the disk is walked instead, leaving
/// out .git, bin, obj and node_modules. Hashes are kept by length and time written, so the second manifest of a tree
/// that didn't change reads no file. Links (symbolic, junctions) are never followed, a tree only has what's in it.
/// </summary>
internal sealed class HexLinkTree
{
    /// <summary>The most files a manifest lists.</summary>
    public const int MOST_FILES = 200_000;

    // Folders a tree that isn't a repository never has in it, what builds and packages make
    private static readonly HashSet<string> SKIPPED = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

    private readonly ConcurrentDictionary<string, (long Length, long Written, string Hash, string? Text, string? Eol)> hashes = new(PlatformPaths.Comparer);

    public HexLinkTree(HexLinkFolder folder)
    {
        Folder = folder;
        Root = Path.GetFullPath(folder.Path);
    }

    public HexLinkFolder Folder { get; }

    /// <summary>The folder's full path here.</summary>
    public string Root { get; }

    /// <summary>Whether the folder is in a git repository, known after the first manifest.</summary>
    public bool InRepository { get; private set; }

    /// <summary>
    /// Every file of the tree with its hash, cached by length and time written. In a repository through
    /// <c>git ls-files -co --exclude-standard -z</c>, otherwise by walking the disk.
    /// </summary>
    public HexLinkManifest Manifest()
    {
        string? head = null, root = null;
        List<string>? relative = null;
        if (HexLinkGit.Why() is null)
        {
            GitResult listed = GitProcess.Run(Root, ["ls-files", "-co", "--exclude-standard", "-z"], patience: TimeSpan.FromMinutes(2));
            if (listed.Ok)
            {
                InRepository = true;
                relative = [.. listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Take(MOST_FILES)];
                GitResult at = GitProcess.Run(Root, ["rev-parse", "HEAD"]);
                head = at.Ok && HexLinkGit.IsCommitId(at.Output.Trim()) ? at.Output.Trim() : null;
                root = head is null ? null : RootOf(head);
            }
        }

        relative ??= Walk();
        var files = new List<HexLinkTreeFile>(relative.Count);
        var seen = new HashSet<string>(PlatformPaths.Comparer);
        foreach (string each in relative)
        {
            if (FileOf(each) is { } file)
            {
                files.Add(file);
                seen.Add(file.Path);
            }
        }

        // What's cached of files no longer there let go of
        if (hashes.Count > files.Count * 2 + 1000)
        {
            foreach (string full in hashes.Keys.Where(full => !File.Exists(full)).ToArray())
                hashes.TryRemove(full, out _);
        }

        return new HexLinkManifest(Folder.Name, head, files, root);
    }

    // The first commit of the repository, worked out once per head (it only moves if history is rewritten). A history
    // with several first commits (subtrees merged in) is known by the least of them, which both sides work out alike.
    // Swapped whole, a manifest is made on more than one thread
    private sealed record RootKnown(string Head, string? Root);

    private volatile RootKnown? rootKnown;

    private string? RootOf(string head)
    {
        if (rootKnown is { } known && known.Head == head)
            return known.Root;
        GitResult roots = GitProcess.Run(Root, ["rev-list", "--max-parents=0", "HEAD"]);
        string? root = roots.Ok
            ? roots.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(HexLinkGit.IsCommitId).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        rootKnown = new RootKnown(head, root);
        return root;
    }

    /// <summary>A file of the tree by its path inside the folder (forward slashes), null when it isn't one the tree carries.</summary>
    public HexLinkTreeFile? FileOf(string relative)
    {
        if (!Plain(relative))
            return null;
        string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;
            string wire = Folder.Name + "/" + relative;
            if (info.Length > HexLinkFiles.MOST_READ)
                return new HexLinkTreeFile(wire, null, info.Length);
            var (hash, text, eol) = HashOf(full, info);
            return new HexLinkTreeFile(wire, hash, info.Length, text, eol);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A path inside the folder that's a plain one, nothing of .git, no .. or a rooted one
    private static bool Plain(string relative) =>
        relative.Length > 0 && relative[0] != '/' && !relative.Contains('\\') && !relative.Contains(':')
        && relative.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && !part.Equals(".git", StringComparison.OrdinalIgnoreCase));

    // A file's hash, and its text's and line ends when it's text with one kind of them, read only when its length or
    // time written moved since it was last hashed
    private (string Hash, string? Text, string? Eol) HashOf(string full, FileInfo info)
    {
        long written = info.LastWriteTimeUtc.Ticks;
        if (hashes.TryGetValue(full, out var known) && known.Length == info.Length && known.Written == written)
            return (known.Hash, known.Text, known.Eol);
        byte[] bytes = File.ReadAllBytes(full);
        string hash = HexLinkFiles.Hash(bytes);
        var (text, eol) = HexLinkSync.TextAndEol(bytes);
        hashes[full] = (info.Length, written, hash, text, eol);
        return (hash, text, eol);
    }

    // The files of a folder that isn't a repository, by walking it, what builds make left out
    private List<string> Walk()
    {
        var found = new List<string>();
        var folders = new Stack<string>();
        folders.Push(Root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
        while (folders.Count > 0 && found.Count < MOST_FILES)
        {
            string at = folders.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(at, "*", options))
                {
                    found.Add(Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/'));
                    if (found.Count >= MOST_FILES)
                        break;
                }

                foreach (string folder in Directory.EnumerateDirectories(at, "*", options))
                {
                    if (!SKIPPED.Contains(Path.GetFileName(folder)))
                        folders.Push(folder);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    /// <summary>
    /// Which of some paths inside the folder the tree leaves out: in a repository what git ignores, otherwise what's
    /// under .git, bin, obj or node_modules.
    /// </summary>
    public HashSet<string> Ignored(IReadOnlyCollection<string> relative)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (string each in relative)
        {
            if (!Plain(each) || (!InRepository && each.Split('/').SkipLast(1).Any(SKIPPED.Contains)))
                ignored.Add(each);
        }

        string[] ask = [.. relative.Where(each => !ignored.Contains(each))];
        if (!InRepository || ask.Length == 0 || HexLinkGit.Why() is not null)
            return ignored;

        // Exit 1 is "none of them", what it lists are the ignored ones
        GitResult said = GitProcess.Run(Root, ["check-ignore", "--stdin", "-z"], string.Join('\0', ask) + "\0");
        foreach (string each in said.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            ignored.Add(each);
        return ignored;
    }

    /// <summary>A manifest as the wire says it.</summary>
    public static JsonObject Json(HexLinkManifest manifest)
    {
        var entries = new JsonArray();
        foreach (HexLinkTreeFile file in manifest.Files)
        {
            var entry = new JsonObject { ["path"] = file.Path, ["size"] = file.Size };
            if (file.Big)
                entry["big"] = true;
            else
                entry["hash"] = file.Hash;
            Text(entry, file.Text, file.Eol);
            entries.Add((JsonNode)entry);
        }

        return new JsonObject { ["folder"] = manifest.Folder, ["head"] = manifest.Head, ["root"] = manifest.Root, ["entries"] = entries };
    }

    /// <summary>A manifest read off the wire with limits, its paths only those of the folder it says.</summary>
    public static HexLinkManifest Of(JsonObject said)
    {
        string folder = HexLinkData.String(said, "folder", 200) ?? string.Empty;
        var files = new List<HexLinkTreeFile>();
        foreach (JsonObject entry in HexLinkData.Objects(said, "entries", MOST_FILES))
        {
            if (HexLinkData.String(entry, "path", HexLinkFiles.MOST_PATH) is not { } path || !path.StartsWith(folder + "/", StringComparison.Ordinal))
                continue;
            string? hash = HexLinkData.Bool(entry, "big") == true ? null : HexLinkData.String(entry, "hash", 64);
            var (text, eol) = hash is null ? (null, null) : TextOf(entry);
            files.Add(new HexLinkTreeFile(path, hash, Math.Max(0, HexLinkData.Long(entry, "size") ?? 0), text, eol));
        }

        string? head = HexLinkData.String(said, "head", 64), root = HexLinkData.String(said, "root", 64);
        return new HexLinkManifest(folder, head is not null && HexLinkGit.IsCommitId(head) ? head : null, files,
            root is not null && HexLinkGit.IsCommitId(root) ? root : null);
    }

    // A file's text hash and line ends said beside its hash, when it has them; a peer from before they were said sends
    // neither, and the sync reads the file instead
    private static void Text(JsonObject entry, string? text, string? eol)
    {
        if (text is null || eol is null)
            return;
        entry["text"] = text;
        entry["eol"] = eol;
    }

    // A text hash and line ends read off the wire, both or neither, only a hash and one of the three words
    private static (string? Text, string? Eol) TextOf(JsonObject entry)
    {
        string? text = HexLinkData.String(entry, "text", 64), eol = HexLinkData.String(entry, "eol", 8);
        return text is { Length: 64 } && text.All(char.IsAsciiHexDigit) && eol is "lf" or "crlf" or "none" ? (text.ToLowerInvariant(), eol) : (null, null);
    }

    /// <summary>A change of a watched tree as sync/changed says it.</summary>
    public static JsonObject ChangeJson(HexLinkTreeChange change)
    {
        if (change.Deleted)
            return new JsonObject { ["path"] = change.Path, ["deleted"] = true };
        if (change.Hash is null)
            return new JsonObject { ["path"] = change.Path, ["big"] = true };
        var said = new JsonObject { ["path"] = change.Path, ["hash"] = change.Hash };
        Text(said, change.Text, change.Eol);
        return said;
    }

    /// <summary>A change read off sync/changed with limits, its path already checked to be of the folder.</summary>
    public static HexLinkTreeChange ChangeOf(string path, JsonObject said)
    {
        if (HexLinkData.Bool(said, "deleted") == true)
            return new HexLinkTreeChange(path, null, Deleted: true);
        string? hash = HexLinkData.String(said, "hash", 64);
        var (text, eol) = hash is null ? (null, null) : TextOf(said);
        return new HexLinkTreeChange(path, hash, Text: text, Eol: eol);
    }
}

/// <summary>
/// A workspace folder's tree watched for the working-tree sync: what changed in it said in batches, a short while
/// (<see cref="Quiet"/>) after the last change, only for files the tree carries, and only when a file's hash really
/// moved. A FileSystemWatcher says where to look; a slow look over the whole tree (<see cref="Rescan"/>) catches what
/// it dropped, which watchers do when a lot changes at once.
/// </summary>
internal sealed class HexLinkTreeWatch : IDisposable
{
    // The most changes a batch says, the rest in the next
    public const int MOST_BATCH = 1000;

    private readonly HexLinkTree tree;
    private readonly Action<IReadOnlyList<HexLinkTreeChange>> changed;
    private readonly FileSystemWatcher? watcher;
    private readonly Timer quiet, rescan;
    private readonly SemaphoreSlim looking = new(1, 1);
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private Dictionary<string, string?> known = new(StringComparer.Ordinal);
    private bool everything, disposed;

    /// <summary>How long after the last change a batch goes, 150 ms.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>How often the whole tree is looked over in case the watcher dropped something, 15 seconds.</summary>
    public TimeSpan Rescan { get; }

    /// <summary>The tree watched, from what <paramref name="from"/> says it is now, told of what changes after.</summary>
    public HexLinkTreeWatch(HexLinkTree tree, HexLinkManifest from, Action<IReadOnlyList<HexLinkTreeChange>> changed, TimeSpan? quietFor = null, TimeSpan? rescanEvery = null)
    {
        this.tree = tree;
        this.changed = changed;
        Quiet = quietFor ?? TimeSpan.FromMilliseconds(150);
        Rescan = rescanEvery ?? TimeSpan.FromSeconds(15);
        foreach (HexLinkTreeFile file in from.Files)
            known[file.Path] = file.Hash;

        quiet = new Timer(_ => _ = Look(), null, Timeout.Infinite, Timeout.Infinite);
        rescan = new Timer(_ => { lock (pending) everything = true; _ = Look(); }, null, Rescan, Rescan);
        try
        {
            watcher = new FileSystemWatcher(tree.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += (_, e) => Moved(e.FullPath);
            watcher.Created += (_, e) => Moved(e.FullPath);
            watcher.Deleted += (_, e) => Moved(e.FullPath);
            watcher.Renamed += (_, e) => { Moved(e.OldFullPath); Moved(e.FullPath); };
            // Events dropped, the whole tree looked over instead
            watcher.Error += (_, _) => { lock (pending) everything = true; Poke(); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // No watcher here (too many already on Linux), the slow look alone
            watcher = null;
        }
    }

    // Something under the folder moved, looked at once it's been quiet a moment
    private void Moved(string full)
    {
        string relative = Path.GetRelativePath(tree.Root, full).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return;
        lock (pending)
        {
            // A folder renamed or deleted takes its files with it, the whole tree looked over
            if (Directory.Exists(full) || pending.Count > 10_000)
                everything = true;
            else
                pending.Add(relative);
        }

        Poke();
    }

    private void Poke()
    {
        if (!disposed)
            quiet.Change(Quiet, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Everything changed so far looked at and said now, the tree looked over whole so nothing a watcher hasn't told
    /// yet is missed, and done when it's been said: what sync/flush answers after.
    /// </summary>
    public Task Flush()
    {
        lock (pending)
            everything = true;
        return Look();
    }

    // What's pending looked at, one look at a time, and what really changed said
    private async Task Look()
    {
        await looking.WaitAsync();
        try
        {
            if (disposed)
                return;
            string[] paths;
            bool whole;
            lock (pending)
            {
                paths = [.. pending];
                pending.Clear();
                whole = everything;
                everything = false;
            }

            var changes = new List<HexLinkTreeChange>();
            if (whole)
            {
                HexLinkManifest now = tree.Manifest();
                var then = known;
                var next = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (HexLinkTreeFile file in now.Files)
                {
                    next[file.Path] = file.Hash;
                    if (!then.TryGetValue(file.Path, out string? was) || was != file.Hash || (file.Big && was is not null))
                        changes.Add(new HexLinkTreeChange(file.Path, file.Hash, Text: file.Text, Eol: file.Eol));
                }

                foreach (string gone in then.Keys.Where(path => !next.ContainsKey(path)))
                    changes.Add(new HexLinkTreeChange(gone, null, Deleted: true));
                known = next;
            }
            else if (paths.Length > 0)
            {
                HashSet<string> ignored = tree.Ignored(paths);
                foreach (string relative in paths)
                {
                    string wire = tree.Folder.Name + "/" + relative;
                    HexLinkTreeFile? file = ignored.Contains(relative) ? null : tree.FileOf(relative);
                    bool had = known.TryGetValue(wire, out string? was);
                    if (file is null)
                    {
                        if (had)
                        {
                            known.Remove(wire);
                            changes.Add(new HexLinkTreeChange(wire, null, Deleted: true));
                        }
                    }
                    else if (!had || was != file.Hash)
                    {
                        known[wire] = file.Hash;
                        changes.Add(new HexLinkTreeChange(wire, file.Hash, Text: file.Text, Eol: file.Eol));
                    }
                }
            }

            for (int at = 0; at < changes.Count; at += MOST_BATCH)
                changed(changes.GetRange(at, Math.Min(MOST_BATCH, changes.Count - at)));
        }
        catch (Exception)
        {
            // Looked at again next time something moves, or at the next slow look
        }
        finally
        {
            looking.Release();
        }
    }

    public void Dispose()
    {
        disposed = true;
        watcher?.Dispose();
        quiet.Dispose();
        rescan.Dispose();
    }
}
