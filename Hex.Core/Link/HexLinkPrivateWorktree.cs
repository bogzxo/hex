using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkDraftStatus(int Files, long Bytes, IReadOnlyList<string> Paths);
internal sealed record HexLinkDraftConflict(string Path, byte[]? Merged);
internal sealed record HexLinkDraftPlan(IReadOnlyList<HexLinkPublishEdit> Edits, IReadOnlyList<HexLinkDraftConflict> Conflicts,
    IReadOnlyDictionary<string, string?> DraftHashes, IReadOnlyDictionary<string, byte[]>? MergeInputs = null);

/// <summary>A Link-owned checkout of a shared tree and its nested repositories. The shared index is never used.</summary>
internal sealed partial class HexLinkPrivateWorktree
{
    private sealed record Repository(string Path, string Commit, HexLinkWorktreeGit Git);
    private sealed record Basis(string Hash, int Repository, string Commit);
    private sealed record FileVersion(string Hash, long Size, string? Text = null)
    {
        public bool SameContent(FileVersion? other) => other is not null &&
            (other.Hash == Hash || Text is not null && Text == other.Text);
    }

    private readonly HexLinkFolder shared;
    private readonly string data;
    private readonly HexLinkTree draftTree;
    private readonly List<Repository> repositories;
    private readonly string[] repositoryLayout;
    private volatile string[] materialized;
    private readonly Dictionary<string, Basis> basis;
    private readonly SemaphoreSlim operation = new(1);
    private readonly Func<HexExcludes>? excludes;

    private HexLinkPrivateWorktree(HexLinkFolder shared, string data, IEnumerable<Repository> repositories, string[] layout,
        Dictionary<string, Basis> basis, Func<HexExcludes>? excludes)
    {
        this.shared = shared;
        this.data = data;
        this.repositories = [.. repositories];
        repositoryLayout = layout;
        materialized = [.. this.repositories.Select(repo => repo.Path)];
        this.basis = basis;
        this.excludes = excludes;
        Root = Path.Combine(data, "tree");
        draftTree = new HexLinkTree(shared with { Path = Root }, excludes);
    }

    public string Root { get; }
    public string GateRoot => Path.Combine(data, "gate");
    public IReadOnlyList<string> RepositoryPaths => repositoryLayout;
    public IReadOnlyList<string> MaterializedRepositories => materialized;
    public bool FirstGate => !File.Exists(Path.Combine(data, "gate.log"));
    public HexLinkFolder SharedFolder => shared;

    public bool HoldIncoming
    {
        get => File.Exists(HoldFile);
        set
        {
            if (value)
                HexLinkFiles.WriteAtomic(HoldFile, []);
            else
                File.Delete(HoldFile);
        }
    }

    private string HoldFile => Path.Combine(data, "hold-incoming");

    public static HexLinkFolder? RecordedFolder(string storage, Guid id)
    {
        string state = Path.Combine(storage, "drafts", id.ToString("N"), "state.json");
        if (!File.Exists(state) || JsonNode.Parse(File.ReadAllBytes(state)) is not JsonObject saved ||
            HexLinkData.String(saved, "source", 4000) is not { } root || !Path.IsPathFullyQualified(root) ||
            HexLinkData.String(saved, "folder", 200) is not { Length: > 0 } name)
            return null;
        return new HexLinkFolder(name, root);
    }
    private string State => Path.Combine(data, "state.json");

    public static async Task<HexLinkPrivateWorktree> Open(string storage, Guid id, HexLinkFolder shared,
        HexLinkManifest? snapshot, Func<HexExcludes>? excludes, CancellationToken cancel,
        IReadOnlySet<string>? selected = null, Action<HexLinkWorktreeProgress>? progress = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A private worktree needs an ID.", nameof(id));
        string home = Path.GetFullPath(storage), source = Path.GetFullPath(shared.Path);
        if (PlatformPaths.IsUnder(home, source) || PlatformPaths.IsUnder(source, home))
            throw new IOException("Private worktree storage must be outside the shared workspace.");
        using var maintenance = await LockStorage(home, cancel);
        string data = Path.Combine(home, "drafts", id.ToString("N"));
        string state = Path.Combine(data, "state.json");
        if (File.Exists(state))
            return await Load(shared, home, data, state, excludes, cancel);
        if (snapshot is not { Certified: true } || snapshot.Folder != shared.Name ||
            (snapshot.Root is null && snapshot.Head is null && snapshot.NestedHeads is not { Count: > 0 }))
            throw new IOException("Private worktrees require a complete snapshot of a shared Git folder.");

        string[] layout = HexLinkWorktreeScope.Layout(snapshot.NestedHeads);
        string[] paths = [.. layout.Where(path => selected is null || path.Length == 0 || selected.Contains(path))];
        Directory.CreateDirectory(data);
        using var setup = SetupLock(data);
        if (File.Exists(state))
            return await Load(shared, home, data, state, excludes, cancel);
        await RecoverSetup(data, home, cancel);

        var draft = new HexLinkPrivateWorktree(shared, data, [], layout, new(StringComparer.Ordinal), excludes);
        await draft.AddRepositories(paths, snapshot, progress, cancel);
        return draft;
    }

    private async Task AddRepositories(string[] paths, HexLinkManifest snapshot, Action<HexLinkWorktreeProgress>? progress, CancellationToken cancel)
    {
        string home = Path.GetDirectoryName(Path.GetDirectoryName(data)!)!;
        string source = Path.GetFullPath(shared.Path);
        var selected = new HashSet<string>(paths, PlatformPaths.Comparer);
        bool Selected(string wire) => selected.Contains(HexLinkWorktreeScope.Nearest(repositoryLayout, Relative(shared, wire)));
        Dictionary<string, FileVersion> versions = await Versions(shared, snapshot, cancel, Selected);
        var grouped = versions.ToLookup(item => HexLinkWorktreeScope.Nearest(repositoryLayout, Relative(shared, item.Key)),
            PlatformPaths.Comparer);

        WriteSetup(paths, source);
        int before = repositories.Count;
        var added = new List<string>();
        try
        {
            // Each repository has its own object store, so they're captured at once, the biggest setting the time rather
            // than the sum of them all. Checked out a level of nesting at a time (git won't add a worktree into a folder
            // a nested one already made), those of a level at once; added to the worktree in their order after
            var versionsOf = paths.Select(relative => grouped[relative].ToArray()).ToArray();
            int started = 0;
            var captured = await Task.WhenAll(paths.Select(async (relative, i) =>
            {
                cancel.ThrowIfCancellationRequested();
                var versionsHere = versionsOf[i];
                var files = new List<HexLinkWorktreeFile>(versionsHere.Length);
                foreach (var (wire, _) in versionsHere)
                {
                    string full = HexLinkFiles.Resolve([shared], wire);
                    bool executable = !OperatingSystem.IsWindows() && File.GetUnixFileMode(full).HasFlag(UnixFileMode.UserExecute);
                    files.Add(new HexLinkWorktreeFile(Inside(relative, Relative(shared, wire)), executable));
                }
                progress?.Invoke(new(relative, Interlocked.Increment(ref started), paths.Length, versionsHere.Length, versionsHere.Sum(item => item.Value.Size)));
                HexLinkWorktreeGit git = await HexLinkWorktreeGit.Open(ObjectDirectory(home, source, relative), cancel);
                string commit = await git.Capture(Path.Combine(source, relative), files, cancel);
                return (Relative: relative, Commit: commit, Git: git);
            }));
            foreach (var level in captured.GroupBy(repo => Depth(repo.Relative)).OrderBy(level => level.Key))
                await Task.WhenAll(level.Select(repo => repo.Git.Checkout(repo.Commit, Path.Combine(Root, repo.Relative), cancel)));
            var made = captured;
            for (int i = 0; i < made.Length; i++)
            {
                int index = repositories.Count;
                repositories.Add(new Repository(made[i].Relative, made[i].Commit, made[i].Git));
                foreach (var (wire, version) in versionsOf[i])
                {
                    basis.Add(wire, new Basis(version.Hash, index, made[i].Commit));
                    added.Add(wire);
                }
            }
            await VerifyCheckout(versions, cancel, Selected);
            cancel.ThrowIfCancellationRequested();
            Save();
            materialized = [.. repositories.Select(repo => repo.Path)];
        }
        catch
        {
            foreach (string wire in added)
                basis.Remove(wire);
            repositories.RemoveRange(before, repositories.Count - before);
            await RecoverSetup(data, home, CancellationToken.None);
            throw;
        }
        File.Delete(SetupFile(data));
        await CompactStorage(force: false, cancel);
    }

    /// <summary>How deep a repository is in the worktree, the top one 0, one a folder of it in 1 and so on.</summary>
    private static int Depth(string relative) =>
        relative.Length == 0 ? 0 : 1 + relative.Count(c => c is '/' or '\\');

    public async Task EnsureRepositories(IReadOnlySet<string> desired, HexLinkManifest snapshot,
        Action<HexLinkWorktreeProgress>? progress, CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            if (!repositoryLayout.SequenceEqual(HexLinkWorktreeScope.Layout(snapshot.NestedHeads), PlatformPaths.Comparer))
                throw new IOException("The shared repository layout changed after this worktree was opened.");
            string[] missing = [.. repositoryLayout.Where(repo => desired.Contains(repo) && !materialized.Contains(repo, PlatformPaths.Comparer))];
            if (missing.Length > 0)
            {
                using var maintenance = await LockStorage(Storage, cancel);
                using var setup = SetupLock(data);
                await RecoverSetup(data, Storage, cancel);
                foreach (string repo in missing)
                {
                    string path = Path.Combine(Root, repo);
                    if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                        throw new IOException($"Save the files already in '{repo}' somewhere safe before opening that private repository.");
                }
                await AddRepositories(missing, snapshot, progress, cancel);
            }
        }
        finally
        {
            operation.Release();
        }
    }

    public async Task<HexLinkDraftStatus> Status(CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            var current = await Current(cancel);
            string[] paths = [.. Changed(current)];
            return new HexLinkDraftStatus(paths.Length, paths.Sum(path => current.GetValueOrDefault(path)?.Size ?? 0), paths);
        }
        finally
        {
            operation.Release();
        }
    }

    public async Task<HexLinkDraftPlan> Prepare(HexLinkManifest snapshot, CancellationToken cancel,
        Func<string, CancellationToken, Task<byte[]>>? readAuthority = null)
    {
        await operation.WaitAsync(cancel);
        try
        {
            Dictionary<string, FileVersion> current = await Current(cancel);
            HexIgnoreFile? authorityRules = await ReadAuthorityRules(snapshot, readAuthority, cancel);
            string[] changed = [.. Changed(current).Where(wire => !IsIgnored(authorityRules, wire))];
            var changedPaths = new HashSet<string>(changed, StringComparer.Ordinal);
            Dictionary<string, FileVersion> theirs = await Versions(shared, snapshot, cancel, changedPaths.Contains);
            var edits = new List<HexLinkPublishEdit>();
            var conflicts = new List<HexLinkDraftConflict>();
            var checkedHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
            var mergeInputs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var mine = shared with { Path = Root };
            foreach (string wire in changed)
            {
                string? before = basis.GetValueOrDefault(wire)?.Hash;
                string? now = theirs.GetValueOrDefault(wire)?.Hash, privateHash = current.GetValueOrDefault(wire)?.Hash;
                if (now == privateHash)
                    continue;
                byte[]? input = await ReadVersion(mine, wire, current.GetValueOrDefault(wire), cancel);
                byte[]? bytes = input;
                string? draftBase = draftMergeBases.GetValueOrDefault(wire);
                if (now != before || draftBase is not null)
                {
                    byte[]? baseline = draftBase is null ? await ReadBasis(wire, cancel)
                        : await ReadDraftMergeBase(wire, draftBase, cancel);
                    byte[]? incoming = theirs.TryGetValue(wire, out var version)
                        ? await ReadGateAuthority(wire, version, readAuthority, cancel) : null;
                    var merged = await HexLinkWorktreeMerge.Merge(baseline, bytes, incoming, cancel);
                    if (merged.Conflict)
                    {
                        conflicts.Add(new HexLinkDraftConflict(wire, merged.Bytes));
                        continue;
                    }
                    bytes = merged.Bytes;
                }
                string? hash = bytes is null ? null : HexLinkFiles.Hash(bytes);
                if (hash != now)
                {
                    edits.Add(new HexLinkPublishEdit(wire, now, bytes));
                    checkedHashes.Add(wire, privateHash);
                    if (input is not null && hash != privateHash)
                        mergeInputs.Add(wire, input);
                }
            }

            return new HexLinkDraftPlan(edits, conflicts, checkedHashes, mergeInputs.Count == 0 ? null : mergeInputs);
        }
        finally
        {
            operation.Release();
        }
    }

    public async Task Follow(HexLinkManifest snapshot, CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            if (HoldIncoming)
                return;
            if (!repositoryLayout.SequenceEqual(HexLinkWorktreeScope.Layout(snapshot.NestedHeads), PlatformPaths.Comparer))
                throw new IOException("The shared repository layout changed after this worktree was opened.");

            var current = await Current(cancel);
            HexIgnoreFile? draftRules = draftTree.LinkIgnore.Read();
            HexIgnoreFile? authorityRules = await ReadAuthorityRules(snapshot, null, cancel);
            var incoming = await Versions(shared, snapshot, cancel, IsMaterialized);
            var sharedTree = new HexLinkTree(shared, excludes);
            var clean = new List<HexLinkPublishEdit>();
            foreach (string wire in incoming.Keys.Union(basis.Keys, StringComparer.Ordinal))
            {
                string? before = basis.GetValueOrDefault(wire)?.Hash;
                string? now = current.GetValueOrDefault(wire)?.Hash;
                FileVersion? next = incoming.GetValueOrDefault(wire);
                if (IsIgnored(draftRules, wire) || IsIgnored(authorityRules, wire) ||
                    (next is null && !sharedTree.Gone(Relative(shared, wire))) ||
                    next?.Hash == before || (now != before && now != next?.Hash))
                    continue;
                byte[]? bytes = await ReadVersion(shared, wire, next, cancel);
                clean.Add(new HexLinkPublishEdit(wire, now, bytes));
            }
            await Adopt(clean, current.ToDictionary(each => each.Key, each => (string?)each.Value.Hash, StringComparer.Ordinal), cancel);
        }
        finally
        {
            operation.Release();
        }
    }

    public async Task Accept(HexLinkDraftPlan published, CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            await Adopt(published.Edits, published.DraftHashes, cancel, published: true, mergeInputs: published.MergeInputs);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task Adopt(IReadOnlyList<HexLinkPublishEdit> edits, IReadOnlyDictionary<string, string?> expectedDrafts,
        CancellationToken cancel, bool published = false, IReadOnlyDictionary<string, byte[]>? mergeInputs = null)
    {
        if (edits.Count == 0)
            return;
        using var maintenance = await LockStorage(Storage, cancel);
        string stage = Path.Combine(data, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var staged = new List<string>();
        var fileVersions = new Dictionary<string, Basis>(StringComparer.Ordinal);
        try
        {
            string[] paths = [.. repositories.Select(repo => repo.Path)];
            foreach (var group in edits.Where(edit => edit.Bytes is not null)
                         .GroupBy(edit => RepositoryIndex(paths, Relative(shared, edit.Path))))
            {
                Repository repo = repositories[group.Key];
                string folder = Path.Combine(stage, group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
                var files = new List<HexLinkWorktreeFile>();
                foreach (HexLinkPublishEdit edit in group)
                {
                    string inside = Inside(repo.Path, Relative(shared, edit.Path));
                    string full = HexLinkFiles.Resolve([new HexLinkFolder("stage", folder)], "stage/" + inside);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    staged.Add(full);
                    await File.WriteAllBytesAsync(full, edit.Bytes!, cancel);
                    files.Add(new HexLinkWorktreeFile(inside));
                }
                string commit = await repo.Git.Capture(folder, files, cancel);
                foreach (HexLinkPublishEdit edit in group)
                    fileVersions[edit.Path] = new Basis(HexLinkFiles.Hash(edit.Bytes!), group.Key, commit);
            }

            var draft = shared with { Path = Root };
            HexIgnoreFile? rules = draftTree.LinkIgnore.Read();
            foreach (HexLinkPublishEdit edit in edits)
            {
                string full = HexLinkFiles.Resolve([draft], edit.Path);
                string? expected = expectedDrafts.GetValueOrDefault(edit.Path);
                string? hash = fileVersions.GetValueOrDefault(edit.Path)?.Hash;
                bool retained = expected != hash && IsIgnored(rules, edit.Path);
                if (expected != hash && !retained)
                {
                    try { await HexLinkFiles.CompareAndReplace(full, edit.Path, expected, edit.Bytes, cancel); }
                    catch (HexLinkException error) when (error.Code == HexLinkErrors.CONFLICT)
                    {
                        retained = true;
                    }
                }
                if (retained && !published)
                    continue;

                // Keep the input that was merged into this receipt if the editor has since changed that input.
                // The next publish can apply only the later draft edits to the published version.
                if (retained && expected is not null && mergeInputs?.TryGetValue(edit.Path, out byte[]? input) == true)
                    RetainDraftMergeBase(edit.Path, expected, input);
                else
                    draftMergeBases.Remove(edit.Path);

                if (fileVersions.TryGetValue(edit.Path, out Basis? version))
                    basis[edit.Path] = version;
                else
                    basis.Remove(edit.Path);
            }
            Save();
        }
        finally
        {
            foreach (string file in staged)
                File.Delete(file);
            foreach (string directory in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                Directory.Delete(directory);
            Directory.Delete(stage);
        }
        PruneDraftMergeBases();
        await CompactStorage(force: false, cancel);
    }

    public async Task Discard(CancellationToken cancel)
    {
        SemaphoreSlim serial = Publications.GetOrAdd(data, static _ => new SemaphoreSlim(1));
        await serial.WaitAsync(cancel);
        try
        {
            await DiscardHeld(cancel);
        }
        finally
        {
            serial.Release();
        }
    }

    private async Task DiscardHeld(CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            using var maintenance = await LockStorage(Storage, cancel);
            if (!Directory.Exists(data))
                return;
            if ((File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The private worktree folder was moved.");
            foreach (Repository repo in repositories.AsEnumerable().Reverse())
            {
                string gate = Path.Combine(GateRoot, repo.Path);
                if (File.Exists(Path.Combine(gate, ".git")))
                    await repo.Git.Remove(gate, cancel);
                string checkout = Path.Combine(Root, repo.Path);
                if (File.Exists(Path.Combine(checkout, ".git")))
                    await repo.Git.Remove(checkout, cancel);
            }
            Directory.Delete(data, recursive: true);
            await CompactStorage(force: true, cancel);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task VerifyCheckout(Dictionary<string, FileVersion> expected, CancellationToken cancel, Func<string, bool> include)
    {
        Dictionary<string, FileVersion> current = await Current(cancel, include);
        foreach (var (wire, version) in expected)
        {
            if (current.GetValueOrDefault(wire)?.Hash == version.Hash)
                continue;
            // Checkout attributes may have adjusted line endings. Restore the exact snapshot bytes.
            byte[]? exact = await ReadBasis(wire, cancel);
            if (exact is null || HexLinkFiles.Hash(exact) != version.Hash)
                throw new IOException($"{wire} changed during the shared snapshot. Try again.");
            string other = HexLinkFiles.Resolve([shared with { Path = Root }], wire);
            await File.WriteAllBytesAsync(other, exact, cancel);
        }

        if (current.Keys.Any(wire => !expected.ContainsKey(wire)))
            throw new IOException("The private checkout contains paths outside the shared snapshot.");
    }

    private bool IsMaterialized(string wire) =>
        materialized.Contains(HexLinkWorktreeScope.Nearest(repositoryLayout, Relative(shared, wire)), PlatformPaths.Comparer);

    private async Task<Dictionary<string, FileVersion>> Current(CancellationToken cancel, Func<string, bool>? include = null, bool verify = false)
    {
        HexLinkManifest snapshot = await Task.Run(() => draftTree.PrivateManifest(_ =>
            include is null ? _ => true : path => include(shared.Name + "/" + path), cancel, verify), cancel);
        if (include is null && snapshot.Files.FirstOrDefault(file => !IsMaterialized(file.Path)) is { } unopened)
            throw new IOException($"'{unopened.Path}' belongs to a private repository that has not been opened. Save the file outside the tree, open its repository and reapply the edit.");
        return await Versions(shared with { Path = Root }, snapshot, cancel);
    }

    private IEnumerable<string> Changed(Dictionary<string, FileVersion> current)
    {
        HexIgnoreFile? rules = draftTree.LinkIgnore.Read();
        return current.Keys.Union(basis.Keys, StringComparer.Ordinal)
            .Where(path => current.GetValueOrDefault(path)?.Hash != basis.GetValueOrDefault(path)?.Hash)
            .Where(path => !IsIgnored(rules, path) && (current.ContainsKey(path) || draftTree.Gone(Relative(shared, path))))
            .Order(StringComparer.Ordinal);
    }

    private bool IsIgnored(HexIgnoreFile? rules, string wire) =>
        HexLinkIgnore.Matches(rules, Relative(shared, wire));

    private async Task<HexIgnoreFile?> ReadAuthorityRules(HexLinkManifest snapshot,
        Func<string, CancellationToken, Task<byte[]>>? readAuthority, CancellationToken cancel)
    {
        if (!snapshot.Certified || snapshot.Folder != shared.Name)
            throw new IOException("The working-tree snapshot is incomplete or belongs to a different folder.");
        string wire = shared.Name + "/" + HexLinkIgnore.FileName;
        if (snapshot.Files.FirstOrDefault(file => PlatformPaths.Comparer.Equals(file.Path, wire)) is not { } file)
            return null;
        RequirePublishSize(file.Path, file.Size);
        if (file.Hash is not { } hash || file.Unavailable)
            throw new GateCandidateChanged([file.Path]);

        byte[] bytes = await ReadGateAuthority(file.Path, new FileVersion(hash, file.Size), readAuthority, cancel);
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false));
        return HexLinkIgnore.Parse(Lines());

        IEnumerable<string> Lines()
        {
            while (reader.ReadLine() is { } line)
                yield return line;
        }
    }

    private async Task<byte[]?> ReadBasis(string wire, CancellationToken cancel)
    {
        if (!basis.TryGetValue(wire, out Basis? before))
            return null;
        Repository repo = repositories[before.Repository];
        return await repo.Git.ReadFile(before.Commit, Inside(repo.Path, Relative(shared, wire)), cancel);
    }

    private static async Task<byte[]?> ReadVersion(HexLinkFolder folder, string wire, FileVersion? version, CancellationToken cancel)
    {
        if (version is null)
            return null;
        RequirePublishSize(wire, version.Size);
        byte[] data = await HexLinkFiles.ReadSharedAsync(HexLinkFiles.Resolve([folder], wire), cancel);
        if (HexLinkFiles.Hash(data) != version.Hash)
            throw new IOException($"{wire} changed during comparison. Try again.");
        return data;
    }

    private static void RequirePublishSize(string wire, long size)
    {
        if (size > HexLinkFiles.MOST_READ)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{wire} exceeds the supported publication file size.");
    }

    private static async Task<Dictionary<string, FileVersion>> Versions(HexLinkFolder folder, HexLinkManifest snapshot, CancellationToken cancel,
        Func<string, bool>? include = null, bool skipUnavailable = false)
    {
        if (!snapshot.Certified || snapshot.Folder != folder.Name)
            throw new IOException("The working-tree snapshot is incomplete or belongs to a different folder.");
        var result = new Dictionary<string, FileVersion>(snapshot.Files.Count, StringComparer.Ordinal);
        foreach (HexLinkTreeFile file in snapshot.Files)
        {
            cancel.ThrowIfCancellationRequested();
            if ((include is not null && !include(file.Path)) || (skipUnavailable && file.Unavailable))
                continue;
            string? hash = file.Hash;
            if (hash is null)
            {
                using var stream = new FileStream(HexLinkFiles.Resolve([folder], file.Path), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
            }
            result.Add(file.Path, new FileVersion(hash, file.Size, file.Text));
        }

        return result;
    }

    private void Save()
    {
        var repos = new JsonArray();
        foreach (Repository repo in repositories)
            repos.Add((JsonNode)new JsonObject { ["path"] = repo.Path, ["commit"] = repo.Commit });
        var files = new JsonObject();
        foreach (var (path, before) in basis)
            files[path] = new JsonObject { ["hash"] = before.Hash, ["repository"] = before.Repository, ["commit"] = before.Commit };
        var state = new JsonObject
        {
            ["source"] = Path.GetFullPath(shared.Path), ["folder"] = shared.Name,
            ["repositories"] = repos, ["layout"] = new JsonArray([.. repositoryLayout.Select(path => (JsonNode)JsonValue.Create(path)!)]),
            ["bases"] = files
        };
        if (draftMergeBases.Count != 0)
            state["draft_merge_bases"] = new JsonObject(draftMergeBases.Select(item =>
                new KeyValuePair<string, JsonNode?>(item.Key, JsonValue.Create(item.Value))));
        HexLinkFiles.WriteAtomic(State, Encoding.UTF8.GetBytes(state.ToJsonString()));
    }

    private static async Task<HexLinkPrivateWorktree> Load(HexLinkFolder shared, string home, string data, string state,
        Func<HexExcludes>? excludes, CancellationToken cancel)
    {
        var document = JsonNode.Parse(await File.ReadAllBytesAsync(state, cancel)) as JsonObject
            ?? throw new IOException("Invalid private worktree metadata.");
        string source = Path.GetFullPath(shared.Path);
        if (HexLinkData.String(document, "source", 4000) is not { } from || !PlatformPaths.Same(from, source) ||
            HexLinkData.String(document, "folder", 200) != shared.Name ||
            document["repositories"] is not JsonArray { Count: > 0 } repos || document["bases"] is not JsonObject files)
            throw new IOException("This private worktree belongs to a different shared folder.");

        var repositories = new Repository[repos.Count];
        for (int i = 0; i < repositories.Length; i++)
        {
            if (repos[i] is not JsonObject entry || HexLinkData.String(entry, "path", 4000) is not { } path ||
                HexLinkData.String(entry, "commit", 64) is not { } commit || !HexLinkGit.IsCommitId(commit) ||
                (i == 0 ? path.Length > 0 : path.Length == 0))
                throw new IOException("Invalid private repository metadata.");
            if (i > 0)
                HexLinkFiles.Resolve([new HexLinkFolder("work", Path.Combine(data, "tree"))], "work/" + path);
            repositories[i] = new Repository(path, commit, await HexLinkWorktreeGit.Open(ObjectDirectory(home, source, path), cancel));
        }

        var basis = new Dictionary<string, Basis>(StringComparer.Ordinal);
        foreach (var (path, node) in files)
        {
            if (node is not JsonObject row || HexLinkData.String(row, "hash", 64) is not { Length: 64 } hash ||
                HexLinkData.Int(row, "repository") is not int repository || repository < 0 || repository >= repositories.Length ||
                HexLinkData.String(row, "commit", 64) is not { } commit || !HexLinkGit.IsCommitId(commit))
                throw new IOException("Invalid private worktree base metadata.");
            basis.Add(path, new Basis(hash, repository, commit));
        }

        string[] layout = document["layout"] is JsonArray layoutRows
            ? [.. layoutRows.Select(row => row is JsonValue value && value.TryGetValue(out string? path) && path is not null
                ? path : throw new IOException("Invalid private repository layout."))]
            : [.. repositories.Select(repo => repo.Path)];
        if (layout.Length == 0 || layout[0] != "" || repositories.Any(repo => !layout.Contains(repo.Path, PlatformPaths.Comparer)))
            throw new IOException("Invalid private repository layout.");
        var draft = new HexLinkPrivateWorktree(shared, data, repositories, layout, basis, excludes);
        draft.LoadDraftMergeBases(document);
        return draft;
    }

    // Keep object stores shallow enough for Git for Windows, including its internal worktree paths.
    private static string ObjectDirectory(string home, string root, string repo) =>
        Path.Combine(home, "objects", HexLinkFiles.Hash(Encoding.UTF8.GetBytes(root + "\n" + repo))[..24]);

    private static string Relative(HexLinkFolder folder, string wire) =>
        wire.StartsWith(folder.Name + "/", StringComparison.Ordinal)
            ? wire[(folder.Name.Length + 1)..] : throw HexLinkException.Invalid("The path is outside the shared folder.");

    private static int RepositoryIndex(string[] paths, string relative)
    {
        string owner = HexLinkWorktreeScope.Nearest(paths, relative);
        return Array.FindIndex(paths, repo => PlatformPaths.Comparer.Equals(repo, owner));
    }

    private static string Inside(string repo, string relative) => repo.Length == 0 ? relative : relative[(repo.Length + 1)..];
}
