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
internal sealed record HexLinkManifest(string Folder, string? Head, IReadOnlyList<HexLinkTreeFile> Files, string? Root = null,
    bool Certified = true, IReadOnlyDictionary<string, string?>? NestedHeads = null, bool? CaseSensitive = null);

/// <summary>
/// A file of a watched tree that changed, its new hash (null with <see cref="Deleted"/> when it went, or when it's too
/// big), and its text's hash and line ends as <see cref="HexLinkTreeFile"/> has them.
/// </summary>
internal sealed record HexLinkTreeChange(string Path, string? Hash, bool Deleted = false, string? Text = null, string? Eol = null, string? PreviousPath = null);

/// <summary>
/// A workspace folder's tree as the working-tree sync sees it: every file git would carry (tracked, and untracked but
/// not ignored, so never bin or obj), hashed. In a folder that isn't a repository the disk is walked instead, leaving
/// out .git, bin, obj and node_modules. Hashes are kept by length and time written, so the second manifest of a tree
/// that didn't change reads no file. Links (symbolic, junctions) are never followed, a tree only has what's in it.
/// </summary>
internal sealed partial class HexLinkTree
{
    /// <summary>The most files a manifest lists.</summary>
    public const int MOST_FILES = 200_000;

    // Leave room for the envelope and worst-case JSON escaping. Count bytes without serializing a second time.
    internal const int PAGE_BUDGET = 4 * 1024 * 1024;

    internal static int WireCost(string path) => 256 + 6 * path.Length;

    // Folders a tree that isn't a repository never has in it, what builds and packages make
    private static readonly HashSet<string> SKIPPED = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

    private readonly ConcurrentDictionary<string, HashStamp> hashes = new(StringComparer.Ordinal);

    private readonly Func<HexExcludes> excludes;
    private readonly ConcurrentDictionary<string, string> movedCase = new(StringComparer.OrdinalIgnoreCase);

    public HexLinkTree(HexLinkFolder folder, Func<HexExcludes>? excludes = null, string? cacheDirectory = null)
    {
        Folder = folder;
        Root = Path.GetFullPath(folder.Path);
        this.excludes = excludes ?? (() => HexExcludes.Usual);
        if (cacheDirectory is not null)
        {
            string key = HexLinkFiles.Hash(System.Text.Encoding.UTF8.GetBytes(Root));
            cacheFile = Path.Combine(cacheDirectory, $"tree-{key}.cache");
        }
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
    public HexLinkManifest Manifest() => Manifest(verify: false);

    internal HexLinkManifest Manifest(bool verify)
    {
        lock (scan)
        {
            RequireRoot();
            LoadCache();
            HexLinkManifest manifest = Scan(verify);
            SaveCache(manifest.Files);
            return manifest;
        }
    }

    private HexLinkManifest Scan(bool verify)
    {
        RequireRoot();
        string? head = null, root = null;
        List<string>? relative = null;
        if (HexLinkGit.Why() is null)
        {
            GitResult listed = GitProcess.Run(Root, ["ls-files", "-co", "--exclude-standard", "-z"], patience: TimeSpan.FromMinutes(2));
            if (listed.Ok && string.IsNullOrWhiteSpace(listed.Error))
            {
                InRepository = true;
                relative = [.. listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Take(MOST_FILES + 1)];
                GitResult at = GitProcess.Run(Root, ["rev-parse", "--verify", "--quiet", "HEAD"]);
                if (at.Exit != 1 && (!at.Ok || !HexLinkGit.IsCommitId(at.Output.Trim())))
                    throw new IOException($"Git could not read HEAD in {Folder.Name}: {at.Why}");
                head = at.Ok ? at.Output.Trim() : null;
                root = head is null ? null : RootOf(head);
            }
            else if (InRepository || !listed.Why.StartsWith("fatal: not a git repository (or any of the parent directories)", StringComparison.Ordinal))
            {
                throw new IOException($"Git could not list {Folder.Name}: {listed.Why}");
            }
        }
        else if (InRepository || HasGitMarker())
        {
            throw new IOException($"Git is unavailable to list {Folder.Name}.");
        }

        bool onDiskNames = relative is null;
        relative ??= Walk();
        // Detect a capped root listing before nested repositories replace any of its entries.
        CheckCount(relative.Count);
        var nested = AddNestedGitFiles(relative);
        CheckCount(relative.Count);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>(relative.Count);
        foreach (string each in relative)
        {
            string path = onDiskNames ? each : CanonicalCase(each);
            if (path != each && IsSoleSpelling(each))
            {
                NoteCase(each);
                path = each;
            }
            if (actual.Add(path))
                paths.Add(path);
        }

        var found = new HexLinkTreeFile?[paths.Count];
        if (paths.Count < 48)
        {
            for (int i = 0; i < paths.Count; i++)
                found[i] = FileOf(paths[i], verify);
        }
        else
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 8) };
            try
            {
                Parallel.For(0, paths.Count, options, i => found[i] = FileOf(paths[i], verify));
            }
            catch (AggregateException error)
            {
                throw new IOException($"Could not read a complete manifest for {Folder.Name}.",
                    error.Flatten().InnerExceptions.FirstOrDefault() ?? error);
            }
        }
        List<HexLinkTreeFile> files = [.. found.OfType<HexLinkTreeFile>()];

        // What's cached of files no longer there let go of
        if (hashes.Count > files.Count * 2 + 1000)
        {
            foreach (string full in hashes.Keys.Where(full => !File.Exists(full)).ToArray())
                hashes.TryRemove(full, out _);
        }

        return new HexLinkManifest(Folder.Name, head, files, root, NestedHeads: nested, CaseSensitive: PlatformPaths.Comparison == StringComparison.Ordinal);
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
        GitResult roots = GitProcess.Run(Root, ["rev-list", "--max-parents=0", head]);
        if (!roots.Ok)
            throw new IOException($"Git could not list the first commits of {Folder.Name}: {roots.Why}");
        string? root = roots.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(HexLinkGit.IsCommitId).Order(StringComparer.Ordinal).FirstOrDefault();
        rootKnown = new RootKnown(head, root);
        return root;
    }

    /// <summary>A file of the tree, or null when the path is absent or excluded. Read failures propagate.</summary>
    public HexLinkTreeFile? FileOf(string relative) => FileOf(relative, verify: false);

    private HexLinkTreeFile? FileOf(string relative, bool verify)
    {
        if (!Plain(relative) || SyncArtifact(relative) || Hidden(relative))
            return null;
        string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        FileInfo? info = RegularFile(full, Root, excludeNonFiles: true);
        if (info is null)
            return null;

        string wire = Folder.Name + "/" + relative;
        if (wire.Length > HexLinkFiles.MOST_PATH)
            throw new IOException($"The path {wire} is too long for a link to list.");
        if (info.Length > HexLinkFiles.MOST_READ)
            return new HexLinkTreeFile(wire, null, info.Length);

        var (hash, text, eol) = HashOf(full, info, verify);
        return new HexLinkTreeFile(wire, hash, info.Length, text, eol);
    }

    // File.Exists suppresses access and I/O failures. Only a filesystem not-found result can establish absence.
    internal static FileInfo? RegularFile(string full, string root, bool excludeNonFiles = false)
    {
        var info = new FileInfo(full);
        FileAttributes attributes = info.Attributes;
        if (attributes == (FileAttributes)(-1))
        {
            try
            {
                attributes = File.GetAttributes(full);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // A lost mount or unavailable workspace is not a deletion of everything in it.
                RequireRoot(root);
                return null;
            }
            info.Refresh();
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
            return info;
        if (excludeNonFiles)
            return null;
        throw new IOException($"{full} is not a regular file.");
    }

    internal bool Gone(string relative)
    {
        if (!Plain(relative) || Hidden(relative))
            return false;
        string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            File.GetAttributes(full);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            RequireRoot();
            return true;
        }
    }

    internal static bool SyncArtifact(string relative)
    {
        ReadOnlySpan<char> name = relative.AsSpan(relative.LastIndexOf('/') + 1);
        return name.Contains(HexLinkSync.THEIRS, StringComparison.OrdinalIgnoreCase) ||
            (!name.IsEmpty && name[0] == '.' && name.EndsWith(".hexlink", StringComparison.OrdinalIgnoreCase)) ||
            (name.Length == 46 && name.StartsWith(".hex-save-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal));
    }

    internal void NoteCase(string relative)
    {
        if (PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase)
            movedCase[relative] = relative;
    }

    internal string CanonicalCase(string relative) => movedCase.TryGetValue(relative, out string? spelling) ? spelling : relative;

    internal bool IsSoleSpelling(string relative) => SoleSpelling(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool SoleSpelling(string full)
    {
        string name = Path.GetFileName(full);
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = false, AttributesToSkip = 0 };
        bool found = false;
        foreach (string path in Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(full)!, name, options))
        {
            string leaf = Path.GetFileName(path);
            if (!leaf.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found || leaf != name)
                return false;
            found = true;
        }

        return found;
    }

    internal static bool OnlyCase(string a, string b) => a != b && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    internal static void RenameCase(string from, string to, string expected)
    {
        string parent = Path.GetDirectoryName(from)!;
        if (!OnlyCase(from, to) || Path.GetDirectoryName(to) != parent)
            throw HexLinkException.Invalid("Link can rename the case of a file within its current directory.");

        // Use the editor/CAS publication gate for both names. Acquire in one order on case-sensitive disks.
        bool sourceFirst = string.CompareOrdinal(from, to) < 0;
        using IDisposable first = HexLinkFiles.LockForLocalWrite(sourceFirst ? from : to);
        using IDisposable? second = PlatformPaths.Comparer.Equals(from, to) ? null : HexLinkFiles.LockForLocalWrite(sourceFirst ? to : from);
        if (!SoleSpelling(from) || RegularFile(from, parent) is null || HexLinkFiles.HashOf(from) != expected)
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The file changed or its name collides with another file.");
        File.Move(from, to, overwrite: false);
    }

    internal void ForgetHash(string relative) =>
        hashes.TryRemove(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)), out _);

    private bool Hidden(string relative, bool folder = false)
    {
        HexExcludes rules = excludes();
        if (rules.Hidden(relative, folder))
            return true;
        for (int slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
        {
            if (rules.Hidden(relative[..slash], folder: true))
                return true;
        }

        return false;
    }

    private void RequireRoot() => RequireRoot(Root);

    private static void RequireRoot(string root)
    {
        FileAttributes attributes = File.GetAttributes(root);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException($"The workspace folder {root} is unavailable.");
    }

    private bool HasGitMarker()
    {
        for (DirectoryInfo? dir = new(Root); dir is not null; dir = dir.Parent)
        {
            try
            {
                File.GetAttributes(Path.Combine(dir.FullName, ".git"));
                return true;
            }
            catch (FileNotFoundException)
            {
            }
        }

        return false;
    }

    internal static void CheckCount(int count)
    {
        if (count > MOST_FILES)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"The working tree exceeds {MOST_FILES:N0} files.");
    }

    // A path inside the folder that's a plain one, nothing of .git, no .. or a rooted one
    private static bool Plain(string relative) =>
        relative.Length > 0 && relative[0] != '/' && !relative.Contains('\\') && !relative.Contains(':')
        && relative.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && !part.Equals(".git", StringComparison.OrdinalIgnoreCase));

    // A file's hash, and its text's and line ends when it's text with one kind of them, read only when its length or
    // time written moved since it was last hashed
    private (string Hash, string? Text, string? Eol) HashOf(string full, FileInfo info, bool verify)
    {
        long written = info.LastWriteTimeUtc.Ticks, created = info.CreationTimeUtc.Ticks, length = info.Length;
        if (!verify && hashes.TryGetValue(full, out HashStamp known) &&
            known.Length == length && known.Written == written && known.Created == created)
            return (known.Hash, known.Text, known.Eol);

        byte[] bytes = HexLinkFiles.ReadShared(full);
        string hash = HexLinkFiles.Hash(bytes);
        var (text, eol) = HexLinkSync.TextAndEol(bytes);
        info.Refresh();
        if (bytes.LongLength != length || info.Length != length || info.LastWriteTimeUtc.Ticks != written || info.CreationTimeUtc.Ticks != created)
            throw new IOException($"{full} changed while Link read its manifest.");
        hashes[full] = new HashStamp(length, written, created, hash, text, eol);
        Interlocked.Increment(ref computedHashes);
        return (hash, text, eol);
    }

    // The files of a folder that isn't a repository, by walking it, what builds make left out
    private List<string> Walk()
    {
        var found = new List<string>();
        var folders = new Stack<string>();
        folders.Push(Root);
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        while (folders.TryPop(out string? at))
        {
            foreach (string file in Directory.EnumerateFiles(at, "*", options))
            {
                string relative = Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (SyncArtifact(relative) || Hidden(relative))
                    continue;
                found.Add(relative);
                CheckCount(found.Count);
            }

            foreach (string child in Directory.EnumerateDirectories(at, "*", options))
            {
                string relative = Path.GetRelativePath(Root, child).Replace(Path.DirectorySeparatorChar, '/');
                if (!SKIPPED.Contains(Path.GetFileName(child)) && !Hidden(relative, folder: true))
                    folders.Push(child);
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
            if (!Plain(each) || SyncArtifact(each) || Hidden(each))
                ignored.Add(each);
        }

        AddGitIgnored(ignored, relative);
        return ignored;
    }

    /// <summary>A manifest as the wire says it.</summary>
    public static JsonObject Json(HexLinkManifest manifest, int start = 0, int? end = null)
    {
        var entries = new JsonArray();
        for (int at = start; at < (end ?? manifest.Files.Count); at++)
        {
            HexLinkTreeFile file = manifest.Files[at];
            var entry = new JsonObject { ["path"] = file.Path, ["size"] = file.Size };
            if (file.Big)
                entry["big"] = true;
            else
                entry["hash"] = file.Hash;
            Text(entry, file.Text, file.Eol);
            entries.Add((JsonNode)entry);
        }

        var said = new JsonObject { ["folder"] = manifest.Folder, ["head"] = manifest.Head, ["root"] = manifest.Root, ["entries"] = entries, ["complete"] = manifest.Certified };
        if (manifest.CaseSensitive is { } caseSensitive)
            said["case_sensitive"] = caseSensitive;
        if (NestedHeadsJson(manifest.NestedHeads) is { } heads)
            said["repositories"] = heads;
        return said;
    }

    /// <summary>A manifest read off the wire with limits, its paths only those of the folder it says.</summary>
    public static HexLinkManifest Of(JsonObject said)
    {
        string folder = HexLinkData.Need(said, "folder", 200);
        if (said["entries"] is not JsonArray entries)
            throw HexLinkException.Invalid("The peer sent a manifest without entries.");
        CheckCount(entries.Count);
        bool certified = HexLinkData.Bool(said, "complete") == true;
        if (said.ContainsKey("complete") && !certified)
            throw HexLinkException.Invalid("The peer reported an incomplete working-tree manifest.");
        if (!certified && entries.Count == MOST_FILES)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, "The peer's manifest reached the legacy file limit and may be incomplete.");

        var files = new List<HexLinkTreeFile>(entries.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string prefix = folder + "/";
        foreach (JsonNode? node in entries)
        {
            if (node is not JsonObject entry ||
                HexLinkData.String(entry, "path", HexLinkFiles.MOST_PATH) is not { } path ||
                !path.StartsWith(prefix, StringComparison.Ordinal) || (certified && !Plain(path[prefix.Length..])) || !paths.Add(path))
            {
                if (certified)
                    throw HexLinkException.Invalid("The peer sent an invalid or duplicate manifest path.");
                continue;
            }

            bool big = HexLinkData.Bool(entry, "big") == true;
            string? hash = big ? null : HexLinkData.String(entry, "hash", 64);
            if (!big && (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)))
                throw HexLinkException.Invalid($"The peer sent an invalid manifest hash for {path}.");
            var (text, eol) = hash is null ? (null, null) : TextOf(entry);
            files.Add(new HexLinkTreeFile(path, hash, Math.Max(0, HexLinkData.Long(entry, "size") ?? 0), text, eol));
        }

        string? head = HexLinkData.String(said, "head", 64), root = HexLinkData.String(said, "root", 64);
        return new HexLinkManifest(folder, head is not null && HexLinkGit.IsCommitId(head) ? head : null, files,
            root is not null && HexLinkGit.IsCommitId(root) ? root : null, certified, NestedHeadsOf(said), HexLinkData.Bool(said, "case_sensitive"));
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
        var said = new JsonObject { ["path"] = change.Path };
        if (change.Hash is null)
            said["big"] = true;
        else
            said["hash"] = change.Hash;
        if (change.PreviousPath is { } previous)
            said["previous"] = previous;
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
        string? previous = HexLinkData.String(said, "previous", HexLinkFiles.MOST_PATH);
        return new HexLinkTreeChange(path, hash, Text: text, Eol: eol, PreviousPath: previous is not null && OnlyCase(path, previous) ? previous : null);
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
    private readonly Dictionary<string, string> caseMoves = new(StringComparer.Ordinal);
    private Dictionary<string, string?> known = new(StringComparer.Ordinal);
    private bool everything, seeded, disposed, verifyOnScan;
    private static readonly TimeSpan VerifyEvery = TimeSpan.FromMinutes(5);
    private long verifyAt = Environment.TickCount64 + (long)VerifyEvery.TotalMilliseconds;

    /// <summary>How long after the last change a batch goes, 150 ms.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>How often the whole tree is looked over in case the watcher dropped something, 15 seconds.</summary>
    public TimeSpan Rescan { get; }

    /// <summary>Collects events immediately, then reports changes from the snapshot passed to Seed.</summary>
    public HexLinkTreeWatch(HexLinkTree tree, Action<IReadOnlyList<HexLinkTreeChange>> changed, TimeSpan? quietFor = null, TimeSpan? rescanEvery = null)
    {
        this.tree = tree;
        this.changed = changed;
        Quiet = quietFor ?? TimeSpan.FromMilliseconds(150);
        Rescan = rescanEvery ?? TimeSpan.FromSeconds(15);
        quiet = new Timer(_ => _ = Look(), null, Timeout.Infinite, Timeout.Infinite);
        rescan = new Timer(_ => { lock (pending) everything = true; _ = Look(); }, null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            watcher = new FileSystemWatcher(tree.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += (_, e) => Moved(e.FullPath);
            watcher.Created += (_, e) => Moved(e.FullPath);
            watcher.Deleted += (_, e) => Moved(e.FullPath);
            watcher.Renamed += (_, e) => Renamed(e.OldFullPath, e.FullPath);
            // Events dropped, the whole tree looked over instead
            watcher.Error += (_, _) =>
            {
                lock (pending)
                    everything = verifyOnScan = true;
                Poke();
            };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // No watcher here (too many already on Linux), the slow look alone
            watcher?.Dispose();
            watcher = null;
        }
    }

    public void Seed(HexLinkManifest from)
    {
        lock (pending)
        {
            if (disposed)
                return;
            foreach (HexLinkTreeFile file in from.Files)
                known[file.Path] = file.Hash;
            seeded = true;
            TimeSpan interval = watcher is not null && Rescan >= TimeSpan.FromSeconds(15) ? Rescan * 3 : Rescan;
            rescan.Change(interval, interval);
            if (watcher is null)
                everything = true;
            if (pending.Count > 0 || everything)
                Poke();
        }
    }

    private void Renamed(string oldFull, string newFull)
    {
        if (HexLinkTree.OnlyCase(oldFull, newFull))
        {
            string before = HexExcludes.Relative(tree.Root, oldFull), after = HexExcludes.Relative(tree.Root, newFull);
            lock (pending)
            {
                if (caseMoves.Remove(before, out string? first))
                    before = first;
                if (before != after)
                    caseMoves[after] = before;
            }
        }

        Moved(oldFull);
        Moved(newFull);
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
        lock (pending)
        {
            if (seeded && !disposed)
                quiet.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Everything changed so far looked at and said now, the tree looked over whole so nothing a watcher hasn't told
    /// yet is missed, and done when it's been said: what sync/flush answers after.
    /// </summary>
    public Task Flush()
    {
        lock (pending)
            everything = true;
        return Look(throwOnFailure: true);
    }

    // What's pending looked at, one look at a time, and what really changed said
    private async Task Look(bool throwOnFailure = false)
    {
        await looking.WaitAsync();
        try
        {
            string[] paths;
            bool whole, verify;
            KeyValuePair<string, string>[] renames;
            lock (pending)
            {
                if (!seeded || disposed)
                    return;
                paths = [.. pending];
                pending.Clear();
                renames = [.. caseMoves];
                caseMoves.Clear();
                whole = everything;
                verify = verifyOnScan || Environment.TickCount64 >= verifyAt;
                everything = verifyOnScan = false;
            }

            foreach (string path in paths)
                tree.ForgetHash(path);
            string prefix = tree.Folder.Name + "/";
            var changes = new List<HexLinkTreeChange>();
            HashSet<string>? replaced = null;
            if (renames.Length > 0)
            {
                HashSet<string> ignoredNames = tree.Ignored(renames.SelectMany(move => new[] { move.Key, move.Value }).ToArray());
                foreach (var (nextName, oldName) in renames)
                {
                    string from = prefix + oldName, to = prefix + nextName;
                    if (!known.ContainsKey(from) || known.ContainsKey(to) || ignoredNames.Contains(nextName) ||
                        ignoredNames.Contains(oldName) || !tree.IsSoleSpelling(nextName) || tree.FileOf(nextName) is not { } file)
                        continue;
                    tree.NoteCase(nextName);
                    replaced ??= new HashSet<string>(StringComparer.Ordinal);
                    replaced.Add(from);
                    replaced.Add(to);
                    changes.Add(new HexLinkTreeChange(to, file.Hash, Text: file.Text, Eol: file.Eol, PreviousPath: from));
                }
            }

            if (whole)
            {
                HexLinkManifest now = tree.Manifest(verify);
                if (verify)
                    verifyAt = Environment.TickCount64 + (long)VerifyEvery.TotalMilliseconds;
                var then = known;
                var next = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (HexLinkTreeFile file in now.Files)
                {
                    next[file.Path] = file.Hash;
                    if (replaced?.Contains(file.Path) != true && (!then.TryGetValue(file.Path, out string? was) || was != file.Hash || (file.Big && was is not null)))
                        changes.Add(new HexLinkTreeChange(file.Path, file.Hash, Text: file.Text, Eol: file.Eol));
                }

                Dictionary<string, int>? added = null;
                foreach (var (path, hash) in then)
                {
                    if (next.ContainsKey(path) || replaced?.Contains(path) == true)
                        continue;
                    added ??= AddedNames(then, changes);
                    if (added.Remove(path, out int index) && index >= 0 &&
                        tree.IsSoleSpelling(changes[index].Path[prefix.Length..]))
                    {
                        HexLinkTreeChange moved = changes[index];
                        changes[index] = moved with { PreviousPath = path };
                        tree.NoteCase(moved.Path[prefix.Length..]);
                        continue;
                    }
                    if (tree.Gone(path[prefix.Length..]))
                        changes.Add(new HexLinkTreeChange(path, null, Deleted: true));
                    else
                        next[path] = hash;
                }
                foreach (HexLinkTreeChange move in changes)
                {
                    if (move.PreviousPath is not { } previous)
                        continue;
                    next.Remove(previous);
                    next[move.Path] = move.Hash;
                }
                known = next;
            }
            else if (paths.Length > 0)
            {
                HashSet<string> ignored = tree.Ignored(paths);
                foreach (string relative in paths)
                {
                    if (ignored.Contains(relative) || tree.CanonicalCase(relative) != relative)
                        continue;
                    string wire = prefix + relative;
                    if (replaced?.Contains(wire) == true)
                        continue;
                    HexLinkTreeFile? file = tree.FileOf(relative);
                    bool had = known.TryGetValue(wire, out string? was);
                    if (file is null)
                    {
                        if (had && tree.Gone(relative))
                            changes.Add(new HexLinkTreeChange(wire, null, Deleted: true));
                    }
                    else if (!had || was != file.Hash)
                    {
                        changes.Add(new HexLinkTreeChange(wire, file.Hash, Text: file.Text, Eol: file.Eol));
                    }
                }

                foreach (HexLinkTreeChange change in changes)
                {
                    if (change.PreviousPath is { } previous)
                        known.Remove(previous);
                    if (change.Deleted)
                        known.Remove(change.Path);
                    else
                        known[change.Path] = change.Hash;
                }
            }

            for (int at = 0; at < changes.Count;)
            {
                int end = at, bytes = 0;
                while (end < changes.Count && end - at < MOST_BATCH)
                {
                    bytes += HexLinkTree.WireCost(changes[end].Path);
                    if (changes[end].PreviousPath is { } previous)
                        bytes += 6 * previous.Length;
                    if (bytes > HexLinkTree.PAGE_BUDGET)
                        break;
                    end++;
                }

                changed(changes.GetRange(at, end - at));
                at = end;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HexLinkException)
        {
            // No incomplete pass changes the baseline. Try the whole tree again on the next event or rescan.
            lock (pending)
                everything = true;
            if (throwOnFailure)
                throw;
        }
        finally
        {
            looking.Release();
        }
    }

    private static Dictionary<string, int> AddedNames(Dictionary<string, string?> known, List<HexLinkTreeChange> changes)
    {
        var added = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < changes.Count; index++)
        {
            HexLinkTreeChange change = changes[index];
            if (change.Deleted || change.PreviousPath is not null || known.ContainsKey(change.Path))
                continue;
            if (!added.TryAdd(change.Path, index))
                added[change.Path] = -1;
        }
        return added;
    }

    public void Dispose()
    {
        lock (pending)
            disposed = true;
        watcher?.Dispose();
        quiet.Dispose();
        rescan.Dispose();
    }
}
