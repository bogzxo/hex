using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Horizon.Core.Platform;

namespace Hex.Core;

/// <summary>One device's held files for a shared folder. The index and its content blobs are local Link state.</summary>
internal sealed class HexLinkHoldStore
{
    private const string StoragePrefix = "sync-local-", BlobSuffix = ".holds", StateSuffix = ".holds.json";
    private const int RootHashLength = 18, FolderHashLength = 8;

    private readonly HexLinkFolder folder;
    private readonly Lock gate = new();
    private long revision;
    private Dictionary<string, HexLinkHold> entries = new(PlatformPaths.Comparer);

    public HexLinkHoldStore(HexLinkFolder folder, string stateDirectory)
    {
        this.folder = folder;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
        if (PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase)
            root = root.ToUpperInvariant();
        string identity = "local-" + HexLinkFiles.Hash(Encoding.UTF8.GetBytes(root))[..RootHashLength];
        string stem = Path.GetFileNameWithoutExtension(HexLinkSync.StateName(identity, folder.Name));
        StateFile = Path.GetFullPath(Path.Combine(stateDirectory, stem + StateSuffix));
        BlobDirectory = Path.GetFullPath(Path.Combine(stateDirectory, stem + BlobSuffix));
        Load();
    }

    public string StateFile { get; }
    public string BlobDirectory { get; }
    public int Count { get { lock (gate) return entries.Count; } }
    internal long Revision => Volatile.Read(ref revision);
    internal string BaseFile(HexLinkHold entry) => Path.Combine(BlobDirectory, entry.BaseHash);
    public event Action? Changed;
    internal event Action<IReadOnlyList<HexLinkHold>, IReadOnlyDictionary<string, string?>?>? Released;

    public HexLinkHold Hold(string path, byte[] baseBytes, HexLinkHoldBaseSource baseSource, bool baseDeleted = false, bool? baseExecutable = null)
    {
        path = Wire(path);
        string full = HexLinkFiles.Resolve([folder], path);
        path = WireFromFullPath(full) ?? throw new ArgumentException("The held path is outside the shared folder.", nameof(path));
        HexLinkHold entry;
        lock (gate)
        {
            if (entries.TryGetValue(path, out HexLinkHold? existing))
                return existing;
            string hash = HexLinkFiles.Hash(baseBytes);
            entry = new(path, DateTimeOffset.UtcNow, hash, baseSource, BaseDeleted: baseDeleted, BaseExecutable: baseExecutable);
            Commit(path, entry, hash, baseBytes);
        }
        Changed?.Invoke();
        return entry;
    }

    internal string? WireFromFullPath(string full)
    {
        string root = HexLinkFiles.Resolve([folder], folder.Name);
        return PlatformPaths.IsUnder(full, root) && !PlatformPaths.Same(full, root)
            ? folder.Name + "/" + Path.GetRelativePath(root, full).Replace('\\', '/') : null;
    }

    public bool IsHeld(string path) => Get(path) is not null;

    public HexLinkHold? Get(string path)
    {
        lock (gate)
            return entries.GetValueOrDefault(path.Replace('\\', '/'));
    }

    public IReadOnlyList<HexLinkHold> List()
    {
        lock (gate)
            return [.. entries.Values.OrderBy(entry => entry.Path, PlatformPaths.Comparer)];
    }

    internal IReadOnlyList<(string Wire, string Relative)> InRepository(string repository)
    {
        var files = new List<(string Wire, string Relative)>();
        foreach (HexLinkHold entry in List())
        {
            string full = Path.Combine(folder.Path, entry.Path[(folder.Name.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
            if (PlatformPaths.IsUnder(full, repository))
                files.Add((entry.Path, Path.GetRelativePath(repository, full).Replace('\\', '/')));
        }
        return files;
    }

    public HexLinkHoldSnapshot? Read(string path)
    {
        lock (gate)
        {
            if (Get(path) is not { } entry)
                return null;
            return new(entry, ReadBlob(entry.BaseHash), entry.IncomingHash is { } incoming ? ReadBlob(incoming) : null);
        }
    }

    public bool SetIncoming(string path, byte[] bytes, string fromDevice) =>
        UpdateIncoming(path, bytes, fromDevice);

    public bool SetIncomingDeleted(string path, string fromDevice) =>
        UpdateIncoming(path, null, fromDevice);

    internal bool SetIncomingStreamed(string path, string staged, string hash, string fromDevice) =>
        UpdateIncoming(path, null, fromDevice, staged, hash);

    private bool UpdateIncoming(string path, byte[]? bytes, string fromDevice, string? staged = null, string? stagedHash = null)
    {
        string? hash = bytes is null ? stagedHash : HexLinkFiles.Hash(bytes);
        bool deleted = hash is null;
        lock (gate)
        {
            if (Get(path) is not { } entry)
                return false;
            if (entry.IncomingHash == hash && entry.IncomingDeleted == deleted)
                return true;
            HexLinkHold next = entry with
            {
                IncomingHash = hash, IncomingDeleted = deleted, IncomingFrom = fromDevice, IncomingAt = DateTimeOffset.UtcNow,
            };
            Commit(entry.Path, next, hash, bytes, staged);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Rebase(string path, byte[] newBaseBytes, bool baseDeleted = false, HexLinkHold? expected = null)
    {
        string hash = HexLinkFiles.Hash(newBaseBytes);
        lock (gate)
        {
            if (Get(path) is not { } entry || expected is not null && !SameVersion(entry, expected))
                return false;
            HexLinkHold next = entry with
            {
                BaseHash = hash, BaseDeleted = baseDeleted, BaseSource = HexLinkHoldBaseSource.Agreed,
                IncomingHash = null, IncomingDeleted = false, IncomingFrom = null, IncomingAt = null, NoticeShown = false,
            };
            Commit(entry.Path, next, hash, newBaseBytes);
        }
        Changed?.Invoke();
        return true;
    }

    public bool MarkNoticeShown(string path)
    {
        lock (gate)
        {
            if (Get(path) is not { HasIncoming: true, NoticeShown: false } entry)
                return false;
            Commit(entry.Path, entry with { NoticeShown = true });
            return true;
        }
    }

    public bool MarkAgedNoticeShown(string path, DateTimeOffset now)
    {
        lock (gate)
        {
            if (Get(path) is not { AgedNoticeShown: false } entry || now - entry.HeldAt < TimeSpan.FromHours(24))
                return false;
            Commit(entry.Path, entry with { AgedNoticeShown = true });
            return true;
        }
    }

    public bool Release(string path) => Get(path) is { } held && Release([held]);

    internal bool Release(IReadOnlyList<HexLinkHold> holds, IReadOnlyDictionary<string, string?>? submitted = null)
    {
        if (holds.Count == 0)
            return true;
        lock (gate)
        {
            if (holds.Any(held => Get(held.Path) is not { } current || !SameVersion(current, held)))
                return false;
            var next = new Dictionary<string, HexLinkHold>(entries, entries.Comparer);
            foreach (HexLinkHold held in holds)
                next.Remove(held.Path);
            Persist(next);
            foreach (HexLinkHold held in holds)
            {
                Retire(held.BaseHash, next);
                Retire(held.IncomingHash, next);
            }
        }
        Released?.Invoke(holds, submitted);
        Changed?.Invoke();
        return true;
    }

    internal bool Matches(HexLinkHold hold) => Get(hold.Path) is { } current && SameVersion(current, hold);

    private static bool SameVersion(HexLinkHold left, HexLinkHold right) =>
        left with { NoticeShown = right.NoticeShown, AgedNoticeShown = right.AgedNoticeShown } == right;

    // Must be called under gate. Persist the new index before retiring any blobs it no longer references.
    private void Commit(string path, HexLinkHold? entry, string? hash = null, byte[]? blob = null, string? staged = null)
    {
        HexLinkHold? previous = entries.GetValueOrDefault(path);
        var next = new Dictionary<string, HexLinkHold>(entries, entries.Comparer);
        if (entry is not null)
            next[path] = entry;
        else
            next.Remove(path);

        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        if (hash is not null && (blob is not null || staged is not null))
        {
            string full = Path.Combine(BlobDirectory, hash);
            if (!File.Exists(full))
            {
                Directory.CreateDirectory(BlobDirectory);
                if (blob is not null)
                    HexLinkFiles.WriteAtomic(full, blob);
                else
                    CopyBlob(staged!, full);
            }
        }

        try
        {
            Persist(next);
        }
        catch
        {
            Retire(hash, entries);
            throw;
        }

        Retire(previous?.BaseHash, next);
        Retire(previous?.IncomingHash, next);
    }

    private void Persist(Dictionary<string, HexLinkHold> next)
    {
        var state = new JsonObject
        {
            ["version"] = 1,
            ["entries"] = new JsonArray([.. next.Values.OrderBy(each => each.Path, StringComparer.Ordinal).Select(WriteEntry)]),
        };
        HexLinkFiles.WriteAtomic(StateFile, Encoding.UTF8.GetBytes(state.ToJsonString()));
        entries = next;
        Interlocked.Increment(ref revision);
    }

    private static void CopyBlob(string from, string to)
    {
        string temp = to + $".{Guid.NewGuid():N}.hexlink";
        try
        {
            File.Copy(from, temp);
            File.Move(temp, to);
        }
        finally { HexLinkGit.TryDelete(temp); }
    }

    private byte[] ReadBlob(string hash) => HexLinkFiles.ReadShared(Path.Combine(BlobDirectory, hash));

    private void Retire(string? hash, Dictionary<string, HexLinkHold> remaining)
    {
        if (hash is not null && !remaining.Values.Any(entry => entry.BaseHash == hash || entry.IncomingHash == hash))
            HexLinkGit.TryDelete(Path.Combine(BlobDirectory, hash));
    }

    private void Load()
    {
        if (!File.Exists(StateFile))
            return;

        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(StateFile)) is not JsonObject state
                || state["version"]?.GetValue<int>() != 1 || state["entries"] is not JsonArray rows)
                throw new FormatException("Invalid Link hold index.");
            var loaded = new Dictionary<string, HexLinkHold>(entries.Comparer);
            foreach (JsonNode? row in rows)
            {
                HexLinkHold entry = ReadEntry(row as JsonObject ?? throw new FormatException("Invalid Link hold entry."));
                if (!loaded.TryAdd(entry.Path, entry))
                    throw new FormatException("Duplicate Link hold path.");
            }
            entries = loaded;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Leave the corrupt index beside the blobs for recovery.
            try { File.Move(StateFile, Path.Combine(Path.GetDirectoryName(StateFile)!,
                $".{Path.GetFileName(StateFile)}.{Guid.NewGuid():N}.corrupt.hexlink")); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { }
        }
        catch (FileNotFoundException)
        {
            // The file disappeared between the existence check and the read.
        }
    }

    private static JsonNode WriteEntry(HexLinkHold entry) => new JsonObject
    {
        ["path"] = entry.Path,
        ["heldAt"] = entry.HeldAt,
        ["baseHash"] = entry.BaseHash,
        ["baseSource"] = entry.BaseSource.ToString().ToLowerInvariant(),
        ["baseDeleted"] = entry.BaseDeleted,
        ["baseExecutable"] = entry.BaseExecutable,
        ["incomingHash"] = entry.IncomingHash,
        ["incomingDeleted"] = entry.IncomingDeleted,
        ["incomingFrom"] = entry.IncomingFrom,
        ["incomingAt"] = entry.IncomingAt is { } at ? JsonValue.Create(at) : null,
        ["noticeShown"] = entry.NoticeShown,
        ["agedNoticeShown"] = entry.AgedNoticeShown,
    };

    private HexLinkHold ReadEntry(JsonObject row)
    {
        string path = Wire(row["path"]?.GetValue<string>() ?? "");
        string? hash = row["baseHash"]?.GetValue<string>(), incoming = row["incomingHash"]?.GetValue<string>();
        bool deleted = row["incomingDeleted"]?.GetValue<bool>() == true;
        HexLinkHoldBaseSource source = row["baseSource"]?.GetValue<string>() switch
        {
            "agreed" => HexLinkHoldBaseSource.Agreed,
            "git" => HexLinkHoldBaseSource.Git,
            "local" => HexLinkHoldBaseSource.Local,
            _ => throw new FormatException("Invalid Link hold base source."),
        };
        if (hash is null || hash.Length != 64 || !Hex(hash)
            || incoming is not null && (incoming.Length != 64 || !Hex(incoming) || deleted)
            || row["heldAt"] is not JsonValue heldAt)
            throw new FormatException("Invalid Link hold hashes or date.");

        return new(path, heldAt.GetValue<DateTimeOffset>(), hash.ToLowerInvariant(), source, incoming?.ToLowerInvariant(), deleted,
            row["incomingFrom"]?.GetValue<string>(), row["incomingAt"]?.GetValue<DateTimeOffset>(),
            row["noticeShown"]?.GetValue<bool>() == true, row["baseDeleted"]?.GetValue<bool>() == true,
            row["baseExecutable"]?.GetValue<bool>(), row["agedNoticeShown"]?.GetValue<bool>() == true);
    }

    private string Wire(string path)
    {
        path = path.Replace('\\', '/');
        int slash = path.IndexOf('/');
        if (slash < 1 || path.Length > HexLinkFiles.MOST_PATH || !PlatformPaths.Comparer.Equals(path[..slash], folder.Name)
            || path[(slash + 1)..].Split('/').Any(part => part is "" or "." or "..")
            || path.Contains(':') || path.Any(char.IsControl))
            throw new ArgumentException("Expected a file path in the shared folder.", nameof(path));
        return folder.Name + path[slash..];
    }

    internal static bool StorageArtifact(ReadOnlySpan<char> name)
    {
        if (name.StartsWith(".sync-local-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".hexlink", StringComparison.OrdinalIgnoreCase)
            && name.IndexOf(StateSuffix, StringComparison.OrdinalIgnoreCase) is >= 0 and var index)
            name = name[1..(index + StateSuffix.Length)];
        int suffix = name.EndsWith(StateSuffix, StringComparison.OrdinalIgnoreCase) ? StateSuffix.Length : BlobSuffix.Length;
        return name.Length > StoragePrefix.Length + RootHashLength + FolderHashLength + suffix + 2
            && name.StartsWith(StoragePrefix, StringComparison.OrdinalIgnoreCase)
            && (suffix == StateSuffix.Length || name.EndsWith(BlobSuffix, StringComparison.OrdinalIgnoreCase))
            && name[StoragePrefix.Length + RootHashLength] == '-'
            && name[^(suffix + FolderHashLength + 1)] == '-'
            && Hex(name.Slice(StoragePrefix.Length, RootHashLength))
            && Hex(name.Slice(name.Length - suffix - FolderHashLength, FolderHashLength));
    }

    internal static bool StoragePath(ReadOnlySpan<char> path)
    {
        if (!path.Contains(BlobSuffix, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (Range component in path.Split('/'))
            if (StorageArtifact(path[component]))
                return true;
        return false;
    }

    private static bool Hex(ReadOnlySpan<char> text)
    {
        foreach (char ch in text)
            if (!char.IsAsciiHexDigit(ch))
                return false;
        return true;
    }
}
