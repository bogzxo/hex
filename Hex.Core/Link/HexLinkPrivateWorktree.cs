using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkDraftStatus(int Files, long Bytes, IReadOnlyList<string> Paths);
internal sealed record HexLinkDraftConflict(string Path, byte[]? Merged);
internal sealed record HexLinkDraftPlan(IReadOnlyList<HexLinkPublishEdit> Edits, IReadOnlyList<HexLinkDraftConflict> Conflicts, IReadOnlyDictionary<string, string?> DraftHashes);

/// <summary>A Link-owned checkout of a shared tree and its nested repositories. The shared index is never used.</summary>
internal sealed partial class HexLinkPrivateWorktree
{
    private sealed record Repository(string Path, string Commit, HexLinkWorktreeGit Git);
    private sealed record Basis(string Hash, int Repository, string Commit);
    private sealed record FileVersion(string Hash, long Size);

    private readonly HexLinkFolder shared;
    private readonly string data;
    private readonly HexLinkTree draftTree;
    private readonly Repository[] repositories;
    private readonly Dictionary<string, Basis> basis;
    private readonly SemaphoreSlim operation = new(1);
    private readonly Func<HexExcludes>? excludes;

    private HexLinkPrivateWorktree(HexLinkFolder shared, string data, Repository[] repositories, Dictionary<string, Basis> basis,
        Func<HexExcludes>? excludes)
    {
        this.shared = shared;
        this.data = data;
        this.repositories = repositories;
        this.basis = basis;
        this.excludes = excludes;
        Root = Path.Combine(data, "tree");
        draftTree = new HexLinkTree(new HexLinkFolder(shared.Name, Root), excludes);
    }

    public string Root { get; }
    public string GateRoot => Path.Combine(data, "gate");
    public IReadOnlyList<string> RepositoryPaths => [.. repositories.Select(repo => repo.Path)];
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
        HexLinkManifest? snapshot, Func<HexExcludes>? excludes, CancellationToken cancel)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A private worktree needs an ID.", nameof(id));
        string home = Path.GetFullPath(storage), source = Path.GetFullPath(shared.Path);
        if (PlatformPaths.IsUnder(home, source) || PlatformPaths.IsUnder(source, home))
            throw new IOException("Private worktree storage must be outside the shared workspace.");
        string data = Path.Combine(home, "drafts", id.ToString("N"));
        string state = Path.Combine(data, "state.json");
        if (File.Exists(state))
            return await Load(shared, home, data, state, excludes, cancel);
        if (snapshot is not { Certified: true } || snapshot.Folder != shared.Name ||
            (snapshot.Root is null && snapshot.Head is null && snapshot.NestedHeads is not { Count: > 0 }))
            throw new IOException("Private worktrees require a complete snapshot of a shared Git folder.");

        string[] paths = ["", .. (snapshot.NestedHeads?.Keys ?? []).OrderBy(path => path.Count(ch => ch == '/')).ThenBy(path => path, StringComparer.Ordinal)];
        var repos = new Repository[paths.Length];
        var files = new List<HexLinkWorktreeFile>[paths.Length];
        for (int i = 0; i < files.Length; i++)
            files[i] = [];
        Dictionary<string, FileVersion> versions = await Versions(shared, snapshot, cancel);
        foreach (string wire in versions.Keys)
        {
            string relative = Relative(shared, wire);
            int index = RepositoryIndex(paths, relative);
            string full = HexLinkFiles.Resolve([shared], wire);
            bool executable = !OperatingSystem.IsWindows() && File.GetUnixFileMode(full).HasFlag(UnixFileMode.UserExecute);
            files[index].Add(new HexLinkWorktreeFile(Inside(paths[index], relative), executable));
        }

        Directory.CreateDirectory(data);
        string destination = Path.Combine(data, "tree");
        var checkout = new List<(HexLinkWorktreeGit Git, string Path)>();
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string relative = paths[i];
                HexLinkWorktreeGit git = await HexLinkWorktreeGit.Open(ObjectDirectory(home, source, relative), cancel);
                string commit = await git.Capture(Path.Combine(source, relative), files[i], cancel);
                repos[i] = new Repository(relative, commit, git);
                string target = Path.Combine(destination, relative);
                await git.Checkout(commit, target, cancel);
                checkout.Add((git, target));
            }

            var bases = new Dictionary<string, Basis>(StringComparer.Ordinal);
            foreach (var (wire, version) in versions)
            {
                int index = RepositoryIndex(paths, Relative(shared, wire));
                bases[wire] = new Basis(version.Hash, index, repos[index].Commit);
            }

            var draft = new HexLinkPrivateWorktree(shared, data, repos, bases, excludes);
            await draft.VerifyCheckout(versions, cancel);
            draft.Save();
            return draft;
        }
        catch
        {
            foreach (var (git, path) in checkout.AsEnumerable().Reverse())
            {
                try { await git.Remove(path, CancellationToken.None); }
                catch (IOException) { }
            }
            throw;
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

    public async Task<HexLinkDraftPlan> Prepare(HexLinkManifest snapshot, CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            Dictionary<string, FileVersion> current = await Current(cancel), theirs = await Versions(shared, snapshot, cancel);
            var edits = new List<HexLinkPublishEdit>();
            var conflicts = new List<HexLinkDraftConflict>();
            var checkedHashes = new Dictionary<string, string?>(StringComparer.Ordinal);
            var mine = new HexLinkFolder(shared.Name, Root);
            foreach (string wire in Changed(current))
            {
                string? before = basis.GetValueOrDefault(wire)?.Hash;
                string? now = theirs.GetValueOrDefault(wire)?.Hash, privateHash = current.GetValueOrDefault(wire)?.Hash;
                if (now == privateHash)
                    continue;
                byte[]? bytes = await ReadVersion(mine, wire, current.GetValueOrDefault(wire), cancel);
                if (now != before)
                {
                    byte[]? baseline = await ReadBasis(wire, cancel);
                    byte[]? incoming = await ReadVersion(shared, wire, theirs.GetValueOrDefault(wire), cancel);
                    var merged = await HexLinkWorktreeMerge.Merge(baseline, bytes, incoming, cancel);
                    if (merged.Conflict)
                    {
                        conflicts.Add(new HexLinkDraftConflict(wire, merged.Bytes));
                        continue;
                    }
                    bytes = merged.Bytes;
                }
                if ((bytes is null ? null : HexLinkFiles.Hash(bytes)) != now)
                {
                    edits.Add(new HexLinkPublishEdit(wire, now, bytes));
                    checkedHashes.Add(wire, privateHash);
                }
            }

            return new HexLinkDraftPlan(edits, conflicts, checkedHashes);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task<HexLinkWorktreeGateResult> Gate(HexLinkManifest snapshot, HexLinkDraftPlan plan,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<HexLinkWorktreeGateOptions>>> gateFor,
        CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            if (plan.Conflicts.Count > 0)
                throw new InvalidOperationException("Settle private worktree conflicts before building.");
            var expectedRepos = new HashSet<string>(repositories.Skip(1).Select(repo => repo.Path), StringComparer.Ordinal);
            if (!expectedRepos.SetEquals(snapshot.NestedHeads?.Keys ?? []))
                throw new IOException("The shared repository layout changed after this worktree was opened.");

            foreach (Repository repo in repositories)
            {
                string path = Path.Combine(GateRoot, repo.Path);
                if (!File.Exists(Path.Combine(path, ".git")))
                    await repo.Git.Checkout(repo.Commit, path, cancel);
            }

            var gate = new HexLinkFolder(shared.Name, GateRoot);
            var tree = new HexLinkTree(gate, excludes);
            var current = await Versions(gate, await Task.Run(tree.Manifest, cancel), cancel);
            var expected = await Versions(shared, snapshot, cancel);
            var candidate = new Dictionary<string, HexLinkPublishEdit>(plan.Edits.Count, StringComparer.Ordinal);
            foreach (HexLinkPublishEdit edit in plan.Edits)
            {
                candidate.Add(edit.Path, edit);
                if (edit.Bytes is { } data)
                    expected[edit.Path] = new FileVersion(HexLinkFiles.Hash(data), data.LongLength);
                else
                    expected.Remove(edit.Path);
            }

            foreach (string gone in current.Keys.Where(path => !expected.ContainsKey(path)))
                File.Delete(HexLinkFiles.Resolve([gate], gone));
            foreach (var (wire, version) in expected)
            {
                if (current.GetValueOrDefault(wire)?.Hash == version.Hash)
                    continue;
                string to = HexLinkFiles.Resolve([gate], wire);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (candidate.GetValueOrDefault(wire)?.Bytes is { } changed)
                    await File.WriteAllBytesAsync(to, changed, cancel);
                else
                    File.Copy(HexLinkFiles.Resolve([shared], wire), to, overwrite: true);
            }
            await VerifyGate(gate, expected, cancel);
            var options = await gateFor([.. plan.Edits.Select(edit => edit.Path)], cancel);
            HexLinkWorktreeGateResult result = await HexLinkWorktreeGate.Run(GateRoot, Path.Combine(data, "gate.log"), options, cancel);
            if (result.Passed)
                await VerifyGate(gate, expected, cancel);
            return result;
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task VerifyGate(HexLinkFolder gate, Dictionary<string, FileVersion> expected, CancellationToken cancel)
    {
        var tree = new HexLinkTree(gate, excludes);
        var scanned = await Versions(gate, await Task.Run(tree.Manifest, cancel), cancel);
        if (scanned.Count != expected.Count || expected.Any(pair => scanned.GetValueOrDefault(pair.Key)?.Hash != pair.Value.Hash))
            throw new IOException("Files changed outside the exact gate candidate. Retry the publication.");
    }

    public async Task Follow(HexLinkManifest snapshot, CancellationToken cancel)
    {
        await operation.WaitAsync(cancel);
        try
        {
            if (HoldIncoming)
                return;

            var current = await Current(cancel);
            var incoming = await Versions(shared, snapshot, cancel);
            var clean = new List<HexLinkPublishEdit>();
            foreach (string wire in incoming.Keys.Union(basis.Keys, StringComparer.Ordinal))
            {
                string? before = basis.GetValueOrDefault(wire)?.Hash;
                string? now = current.GetValueOrDefault(wire)?.Hash;
                FileVersion? next = incoming.GetValueOrDefault(wire);
                if (next?.Hash == before || (now != before && now != next?.Hash))
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
            await Adopt(published.Edits, published.DraftHashes, cancel);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task Adopt(IReadOnlyList<HexLinkPublishEdit> edits, IReadOnlyDictionary<string, string?> expectedDrafts, CancellationToken cancel)
    {
        if (edits.Count == 0)
            return;
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

            var draft = new HexLinkFolder(shared.Name, Root);
            foreach (HexLinkPublishEdit edit in edits)
            {
                string full = HexLinkFiles.Resolve([draft], edit.Path);
                string? expected = expectedDrafts.GetValueOrDefault(edit.Path);
                string? hash = fileVersions.GetValueOrDefault(edit.Path)?.Hash;
                if (expected != hash)
                {
                    try { await HexLinkFiles.CompareAndReplace(full, edit.Path, expected, edit.Bytes, cancel); }
                    catch (HexLinkException error) when (error.Code == HexLinkErrors.CONFLICT)
                    {
                        // A new edit keeps the base it was made against.
                        continue;
                    }
                }
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
        if (await Pending(cancel) is not null)
            throw new IOException("Recover the pending publication before discarding this private worktree.");
        await operation.WaitAsync(cancel);
        try
        {
            foreach (Repository repo in repositories.Reverse())
            {
                string gate = Path.Combine(GateRoot, repo.Path);
                if (File.Exists(Path.Combine(gate, ".git")))
                    await repo.Git.Remove(gate, cancel);
                await repo.Git.Remove(Path.Combine(Root, repo.Path), cancel);
            }
            File.Delete(Path.Combine(data, "gate.log"));
            File.Delete(HoldFile);
            File.Delete(State);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task VerifyCheckout(Dictionary<string, FileVersion> expected, CancellationToken cancel)
    {
        Dictionary<string, FileVersion> current = await Current(cancel);
        foreach (var (wire, version) in expected)
        {
            if (current.GetValueOrDefault(wire)?.Hash == version.Hash)
                continue;
            // Checkout attributes may have adjusted line endings. Restore the exact snapshot bytes.
            byte[]? exact = await ReadBasis(wire, cancel);
            if (exact is null || HexLinkFiles.Hash(exact) != version.Hash)
                throw new IOException($"{wire} changed during the shared snapshot. Try again.");
            string other = HexLinkFiles.Resolve([new HexLinkFolder(shared.Name, Root)], wire);
            await File.WriteAllBytesAsync(other, exact, cancel);
        }

        if (current.Keys.Any(wire => !expected.ContainsKey(wire)))
            throw new IOException("The private checkout contains paths outside the shared snapshot.");
    }

    private async Task<Dictionary<string, FileVersion>> Current(CancellationToken cancel) =>
        await Versions(new HexLinkFolder(shared.Name, Root), await Task.Run(draftTree.Manifest, cancel), cancel);

    private IEnumerable<string> Changed(Dictionary<string, FileVersion> current) =>
        current.Keys.Union(basis.Keys, StringComparer.Ordinal)
            .Where(path => current.GetValueOrDefault(path)?.Hash != basis.GetValueOrDefault(path)?.Hash)
            .Order(StringComparer.Ordinal);

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
        if (version.Size > HexLinkFiles.MOST_READ)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{wire} exceeds the supported publication file size.");
        byte[] data = await HexLinkFiles.ReadSharedAsync(HexLinkFiles.Resolve([folder], wire), cancel);
        if (HexLinkFiles.Hash(data) != version.Hash)
            throw new IOException($"{wire} changed during comparison. Try again.");
        return data;
    }

    private static async Task<Dictionary<string, FileVersion>> Versions(HexLinkFolder folder, HexLinkManifest snapshot, CancellationToken cancel)
    {
        if (!snapshot.Certified || snapshot.Folder != folder.Name)
            throw new IOException("The working-tree snapshot is incomplete or belongs to a different folder.");
        var result = new Dictionary<string, FileVersion>(snapshot.Files.Count, StringComparer.Ordinal);
        foreach (HexLinkTreeFile file in snapshot.Files)
        {
            cancel.ThrowIfCancellationRequested();
            string? hash = file.Hash;
            if (hash is null)
            {
                using var stream = new FileStream(HexLinkFiles.Resolve([folder], file.Path), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
            }
            result.Add(file.Path, new FileVersion(hash, file.Size));
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
            ["repositories"] = repos, ["bases"] = files
        };
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

        return new HexLinkPrivateWorktree(shared, data, repositories, basis, excludes);
    }

    // Keep object stores shallow enough for Git for Windows, including its internal worktree paths.
    private static string ObjectDirectory(string home, string root, string repo) =>
        Path.Combine(home, "objects", HexLinkFiles.Hash(Encoding.UTF8.GetBytes(root + "\n" + repo))[..24]);

    private static string Relative(HexLinkFolder folder, string wire) =>
        wire.StartsWith(folder.Name + "/", StringComparison.Ordinal)
            ? wire[(folder.Name.Length + 1)..] : throw HexLinkException.Invalid("The path is outside the shared folder.");

    private static int RepositoryIndex(string[] paths, string relative)
    {
        for (int i = paths.Length - 1; i > 0; i--)
        {
            if (relative.StartsWith(paths[i] + "/", StringComparison.Ordinal))
                return i;
        }
        return 0;
    }

    private static string Inside(string repo, string relative) => repo.Length == 0 ? relative : relative[(repo.Length + 1)..];
}
