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
    public event Action? Changed;

    public HexLinkHold Hold(string path, byte[] baseBytes, HexLinkHoldBaseSource baseSource, bool baseDeleted = false)
    {
        path = Wire(path);
        _ = HexLinkFiles.Resolve([folder], path);
        HexLinkHold entry;
        lock (gate)
        {
            if (entries.TryGetValue(path, out HexLinkHold? existing))
                return existing;
            string hash = HexLinkFiles.Hash(baseBytes);
            entry = new(path, DateTimeOffset.UtcNow, hash, baseSource, BaseDeleted: baseDeleted);
            Commit(path, entry, hash, baseBytes);
        }
        Changed?.Invoke();
        return entry;
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

    private bool UpdateIncoming(string path, byte[]? bytes, string fromDevice)
    {
        string? hash = bytes is null ? null : HexLinkFiles.Hash(bytes);
        lock (gate)
        {
            if (Get(path) is not { } entry)
                return false;
            if (entry.IncomingHash == hash && entry.IncomingDeleted == (bytes is null))
                return true;
            HexLinkHold next = entry with
            {
                IncomingHash = hash, IncomingDeleted = bytes is null, IncomingFrom = fromDevice, IncomingAt = DateTimeOffset.UtcNow,
            };
            Commit(entry.Path, next, hash, bytes);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Rebase(string path, byte[] newBaseBytes, bool baseDeleted = false)
    {
        string hash = HexLinkFiles.Hash(newBaseBytes);
        lock (gate)
        {
            if (Get(path) is not { } entry)
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

    public bool Release(string path)
    {
        lock (gate)
        {
            if (Get(path) is not { } entry)
                return false;
            Commit(entry.Path, null);
        }
        Changed?.Invoke();
        return true;
    }

    // Must be called under gate. Persist the new index before retiring any blobs it no longer references.
    private void Commit(string path, HexLinkHold? entry, string? hash = null, byte[]? blob = null)
    {
        HexLinkHold? previous = entries.GetValueOrDefault(path);
        var next = new Dictionary<string, HexLinkHold>(entries, entries.Comparer);
        if (entry is not null)
            next[path] = entry;
        else
            next.Remove(path);

        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        if (hash is not null && blob is not null)
        {
            string full = Path.Combine(BlobDirectory, hash);
            if (!File.Exists(full))
            {
                Directory.CreateDirectory(BlobDirectory);
                HexLinkFiles.WriteAtomic(full, blob);
            }
        }

        try
        {
            var state = new JsonObject
            {
                ["version"] = 1,
                ["entries"] = new JsonArray([.. next.Values.OrderBy(each => each.Path, StringComparer.Ordinal).Select(WriteEntry)]),
            };
            HexLinkFiles.WriteAtomic(StateFile, Encoding.UTF8.GetBytes(state.ToJsonString()));
        }
        catch
        {
            Retire(hash, entries);
            throw;
        }

        entries = next;
        Retire(previous?.BaseHash, next);
        Retire(previous?.IncomingHash, next);
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
            try { File.Move(StateFile, StateFile + $".{Guid.NewGuid():N}.corrupt.hexlink"); }
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
        ["incomingHash"] = entry.IncomingHash,
        ["incomingDeleted"] = entry.IncomingDeleted,
        ["incomingFrom"] = entry.IncomingFrom,
        ["incomingAt"] = entry.IncomingAt is { } at ? JsonValue.Create(at) : null,
        ["noticeShown"] = entry.NoticeShown,
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
            row["noticeShown"]?.GetValue<bool>() == true, row["baseDeleted"]?.GetValue<bool>() == true);
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
