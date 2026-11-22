using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Core;

internal enum HexLinkProvisionPhase
{
    Planning,
    Repositories,
    Files,
    Sync,
    Complete
}

internal sealed record HexLinkProvisionProgress(HexLinkProvisionPhase Phase, int RepositoryIndex, int RepositoryCount,
    long BytesDone = 0, long BytesTotal = 0, int FilesDone = 0, int FilesTotal = 0, IReadOnlyList<HexLinkProvisionSkip>? Skipped = null);

internal sealed record HexLinkProvisionFolder(string Name, string Path, HexLinkAuthority? Authority = null);

internal sealed record HexLinkProvisionSkip(string Path, string Reason, bool File = false);

internal sealed record HexLinkProvisionResult(IReadOnlyList<HexLinkFolder> Folders, IReadOnlyList<HexLinkProvisionSkip> Skipped);

internal sealed record HexLinkProvisionRepository(string Id, string Path, string Head, string? Branch, string? Origin,
    string? ParentRepository = null);

internal sealed record HexLinkProvisionPlan(IReadOnlyList<HexLinkProvisionFolder> Folders, IReadOnlyList<HexLinkProvisionRepository> Repositories,
    IReadOnlyList<string>? Deleted = null, IReadOnlyList<string>? LargeChanges = null, string? RepositoryPath = null, IReadOnlyList<HexLinkProvisionSkip>? Skipped = null, bool WorktreeDeferred = false)
{
    internal const string BUNDLE_PREFIX = "provision-";

    internal static bool IsBundleId(string id) => id.StartsWith(BUNDLE_PREFIX, StringComparison.Ordinal)
        && id.AsSpan(BUNDLE_PREFIX.Length) is { Length: > 33 } suffix && suffix[32] == '-'
        && Guid.TryParseExact(suffix[..32], "N", out _) && int.TryParse(suffix[33..], out _);

    public JsonObject ToJson() => new()
    {
        ["folders"] = new JsonArray([.. Folders.Select(folder => (JsonNode)new JsonObject { ["name"] = folder.Name, ["path"] = folder.Path, ["authority"] = folder.Authority?.ToJson() })]),
        ["repositories"] = new JsonArray([.. Repositories.Select(repo => (JsonNode)new JsonObject
        {
            ["id"] = repo.Id, ["path"] = repo.Path, ["head"] = repo.Head,
            ["branch"] = repo.Branch, ["origin"] = repo.Origin, ["parent"] = repo.ParentRepository
        })]),
        ["deleted"] = new JsonArray([.. (Deleted ?? []).Select(path => (JsonNode)path)]),
        ["largeChanges"] = new JsonArray([.. (LargeChanges ?? []).Select(path => (JsonNode)path)]),
        ["repository_path"] = RepositoryPath,
        ["worktree_deferred"] = WorktreeDeferred,
        ["skipped"] = new JsonArray([.. (Skipped ?? []).Select(entry => (JsonNode)new JsonObject
        {
            ["path"] = entry.Path, ["reason"] = entry.Reason.Length <= 1024 ? entry.Reason : entry.Reason[..1024], ["file"] = entry.File
        })])
    };

    public static HexLinkProvisionPlan FromJson(JsonObject json)
    {
        var folders = new List<HexLinkProvisionFolder>();
        var repositories = new List<HexLinkProvisionRepository>();
        if (json["folders"] is not JsonArray { Count: > 0 and <= HexLinkHost.MOST_TREES } folderEntries
            || json["repositories"] is not JsonArray { Count: <= HexLinkTree.MOST_REPOS } repositoryEntries)
            throw HexLinkException.Invalid("The workspace plan has an invalid number of folders or repositories.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? entry in folderEntries)
        {
            if (entry is not JsonObject folder)
                throw HexLinkException.Invalid("The workspace plan has an invalid folder.");
            string name = HexLinkData.Need(folder, "name", 201), path = HexLinkData.Need(folder, "path", HexLinkFiles.MOST_PATH + 1);
            if (name.Length is 0 or > 200 || name.Contains('/') || name.Contains('\\') || !names.Add(name))
                throw HexLinkException.Invalid("The workspace plan has an invalid or repeated folder name.");
            folders.Add(new HexLinkProvisionFolder(name, path, HexLinkAuthority.Of(HexLinkData.Object(folder, "authority"))));
        }
        foreach (JsonNode? entry in repositoryEntries)
        {
            if (entry is not JsonObject repo)
                throw HexLinkException.Invalid("The workspace plan has an invalid repository.");
            string id = HexLinkData.Need(repo, "id", 80), path = HexLinkData.Need(repo, "path", HexLinkFiles.MOST_PATH + 1);
            string sha = HexLinkData.Need(repo, "head", 65);
            if (!IsBundleId(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                || !ids.Add(id) || !paths.Add(path) || !HexLinkGit.IsCommitId(sha))
                throw HexLinkException.Invalid("The workspace plan has an invalid or repeated repository, or an invalid commit.");
            var record = new HexLinkProvisionRepository(id, path, sha, HexLinkData.String(repo, "branch", 1024),
                HexLinkData.String(repo, "origin", 8192), HexLinkData.String(repo, "parent", HexLinkFiles.MOST_PATH + 1));
            repositories.Add(record);
        }

        foreach (HexLinkProvisionRepository repo in repositories)
        {
            if (!folders.Any(folder => Inside(repo.Path, folder.Path))
                || repo.ParentRepository is { } parent && (parent == repo.Path || !paths.Contains(parent) || !Inside(repo.Path, parent)))
                throw HexLinkException.Invalid("A repository in the plan is outside its folder or its submodule parent.");
        }

        var skipped = new List<HexLinkProvisionSkip>();
        if (json["skipped"] is not null)
        {
            if (json["skipped"] is not JsonArray { Count: <= HexLinkTree.MOST_REPOS } entries)
                throw HexLinkException.Invalid("The workspace plan has an invalid list of skipped repositories.");
            foreach (JsonNode? entry in entries)
            {
                if (entry is not JsonObject skip)
                    throw HexLinkException.Invalid("The workspace plan has an invalid skipped repository.");
                string path = HexLinkData.Need(skip, "path", HexLinkFiles.MOST_PATH + 1);
                string reason = HexLinkData.Need(skip, "reason", 1025);
                if (path.Length == 0 || reason.Length == 0 || RootOf(folders, path) || !folders.Any(folder => Inside(path, folder.Path)))
                    throw HexLinkException.Invalid("A skipped repository must be inside a workspace folder.");
                skipped.Add(new(path, reason, HexLinkData.Bool(skip, "file") == true));
            }
        }

        return new HexLinkProvisionPlan(folders, repositories, Paths("deleted"), Paths("largeChanges"),
            HexLinkData.String(json, "repository_path", HexLinkFiles.MOST_PATH + 1), skipped,
            HexLinkData.Bool(json, "worktree_deferred") == true);

        List<string>? Paths(string field)
        {
            if (json[field] is null)
                return null;
            if (json[field] is not JsonArray { Count: <= HexLinkTree.MOST_FILES } entries)
                throw HexLinkException.Invalid("The workspace plan has an invalid file list.");
            var result = HexLinkData.Strings(json, field, HexLinkTree.MOST_FILES, HexLinkFiles.MOST_PATH + 1);
            if (result.Count != entries.Count || result.Any(path => path.Length is 0 or > HexLinkFiles.MOST_PATH))
                throw HexLinkException.Invalid("The workspace plan has an invalid file path.");
            return result;
        }
    }

    internal static bool RootOf(IReadOnlyList<HexLinkProvisionFolder> folders, string path) =>
        folders.Any(folder => folder.Path == path) && !folders.Any(folder => folder.Path != path && Inside(path, folder.Path));

    internal static bool Skips(IEnumerable<HexLinkProvisionSkip> skipped, string path) =>
        skipped.Any(entry => !entry.File && Inside(path, entry.Path));

    internal static bool Inside(string path, string within) =>
        within.Length == 0 || path.Equals(within, StringComparison.Ordinal) || path.StartsWith(within + "/", StringComparison.Ordinal);
}

internal sealed record HexLinkHostedProvision(HexLinkProvisionPlan Plan, IReadOnlyDictionary<string, string> WirePaths);

internal interface IHexLinkBundleSource
{
    Task<string?> TransferBundle(string repository, IReadOnlyList<string> have, Action<long, long> progress, string? verifyIn, CancellationToken cancel);
}

internal interface IHexLinkProvisionOffers
{
    void WorkspaceOffered(HexLinkDevice device, string workspace);
    HexLinkWorkspace WorkspaceForProvision(string? folder);
}

internal sealed partial class HexLinkPeer : IHexLinkBundleSource
{
    internal async Task<HexLinkProvisionPlan> GetProvisionPlan(string? folder, CancellationToken cancel, string? repositoryPath = null)
    {
        string id = Guid.NewGuid().ToString("N");
        try
        {
            JsonObject response = await AskObject("git/bundle",
                new JsonObject { ["operation"] = "plan", ["folder"] = folder, ["repository_path"] = repositoryPath,
                      ["defer_worktree"] = folder is null, ["id"] = id }, cancel, TimeSpan.FromMinutes(5));
            return HexLinkProvisionPlan.FromJson(response);
        }
        catch (Exception e) when (e is OperationCanceledException or HexLinkException { Code: HexLinkErrors.TIMED_OUT })
        {
            session.Tell("git/cancel", new JsonObject { ["id"] = id });
            throw;
        }
    }

    internal Task ShareProvision(string workspace, CancellationToken cancel) =>
        AskObject("git/bundle", new JsonObject { ["operation"] = "offer", ["workspace"] = workspace }, cancel);

    Task<string?> IHexLinkBundleSource.TransferBundle(string repository, IReadOnlyList<string> have, Action<long, long> progress, string? verifyIn, CancellationToken cancel) =>
        ProvisionBundle(repository, have, progress, verifyIn, cancel);
}

internal static partial class HexLinkProvision
{
    private static readonly TimeSpan GIT_PATIENCE = TimeSpan.FromMinutes(10);

    private sealed record Checkout(HexLinkProvisionRepository Remote, string Directory, string? Head, string? Branch, List<string> Have)
    {
        public string? Bundle { get; set; }
        public bool Existing { get; init; }
    }

    private sealed record FileTransfer(string Relative, string? Before, HexLinkTreeFile? Remote, bool IncludeSystemFiles);

    public static async Task<HexLinkProvisionResult> Get(HexLinkPeer peer, string destination, string journal,
        IProgress<HexLinkProvisionProgress>? progress, CancellationToken cancel, IHexLinkBundleSource? bundles = null,
        Func<HexLinkFolder, HexLinkFolder>? folderPolicy = null)
    {
        progress?.Report(new HexLinkProvisionProgress(HexLinkProvisionPhase.Planning, 0, 0));
        ValidateDestination(destination, cancel);
        HexLinkProvisionPlan plan = await peer.GetProvisionPlan(null, cancel);
        if (plan.Deleted is null || plan.LargeChanges is null)
            throw new InvalidOperationException("The host needs a Hex version that can send a complete workspace.");
        var skipped = new List<HexLinkProvisionSkip>(plan.Skipped ?? []);
        foreach (HexLinkProvisionFolder folder in plan.Folders)
        {
            try
            {
                Resolve(destination, folder.Path);
            }
            catch (Exception error) when (!HexLinkProvisionPlan.RootOf(plan.Folders, folder.Path) && ItemFailure(error, peer))
            {
                skipped.Add(new(folder.Path, error.Message));
            }
        }
        plan = WithoutSkipped(plan, skipped);
        if (!await peer.SupportsProvisionBulk(cancel))
        {
            foreach (string large in plan.LargeChanges ?? [])
                if (!HexLinkProvisionPlan.Skips(skipped, large))
                    skipped.Add(new(large, "The working file exceeds this link's 8 MB transfer limit.", File: true));
        }
        progress?.Report(new(HexLinkProvisionPhase.Planning, 0, plan.Repositories.Count, Skipped: skipped.ToArray()));

        await ApplyRepositories(bundles ?? peer, plan, destination, align: false, progress, cancel, skipped);
        plan = WithoutSkipped(plan, skipped);
        if (plan.WorktreeDeferred)
        {
            var (deleted, notes) = await peer.ProvisionDeleted(plan.Repositories, cancel);
            skipped.AddRange(notes);
            plan = plan with { Deleted = deleted };
        }
        HexLinkFolder[] local = [.. plan.Folders.Select(folder => new HexLinkFolder(folder.Name, Resolve(destination, folder.Path), folder.Authority))];
        if (folderPolicy is not null)
            local = [.. local.Select(folderPolicy)];
        foreach (HexLinkFolder folder in local)
            Directory.CreateDirectory(folder.Path);
        await CopyWorkingTrees(peer, plan, local, destination, journal, progress, cancel, skipped: skipped);
        return new(local, SortedSkips(skipped));
    }

    private static HexLinkProvisionPlan WithoutSkipped(HexLinkProvisionPlan plan, IReadOnlyList<HexLinkProvisionSkip> skipped) =>
        skipped.Count == 0 ? plan : plan with
        {
            Folders = [.. plan.Folders.Where(folder => !HexLinkProvisionPlan.Skips(skipped, folder.Path))],
            Repositories = [.. plan.Repositories.Where(repo => !HexLinkProvisionPlan.Skips(skipped, repo.Path))],
            Skipped = skipped
        };

    private static void ValidateDestination(string destination, CancellationToken cancel)
    {
        string full = Resolve(destination, string.Empty);
        string? parent = Path.GetDirectoryName(full);
        if (parent is null || File.Exists(full))
            throw new InvalidOperationException("Choose a workspace directory beneath a drive or home directory.");

        while (parent is not null && !Directory.Exists(parent))
            parent = Path.GetDirectoryName(parent);
        if (parent is null)
            throw new InvalidOperationException("Choose a destination on a mounted filesystem.");

        GitResult git = GitProcess.Run(parent, ["rev-parse", "--show-toplevel"], cancel: cancel);
        if (git.Ok)
            throw new InvalidOperationException($"The destination is inside another Git repository ({git.Output.Trim()}).");
        if (!git.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Couldn't check the destination: {HexLinkGit.ErrorOf(git)}");
    }

    private static async Task CopyWorkingTrees(HexLinkPeer peer, HexLinkProvisionPlan plan, IReadOnlyList<HexLinkFolder> folders,
        string destination, string journal, IProgress<HexLinkProvisionProgress>? progress, CancellationToken cancel, IReadOnlySet<string>? repos = null, List<HexLinkProvisionSkip>? skipped = null)
    {
        JsonObject previous = File.Exists(journal) ? JsonNode.Parse(File.ReadAllBytes(journal)) as JsonObject
            ?? throw new IOException("The workspace transfer journal is damaged.") : [];
        bool bulk = await peer.SupportsProvisionBulk(cancel);
        var transfers = new Dictionary<string, FileTransfer>(StringComparer.OrdinalIgnoreCase);
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HexLinkProvisionRepository repository in plan.Repositories)
        {
            foreach (string status in Status(Resolve(destination, repository.Path), cancel))
                dirty.Add(Relative(repository.Path, status[3..]));
        }
        var deleted = (plan.Deleted ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var withheldFiles = (skipped ?? []).Where(entry => entry.File).Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < folders.Count; i++)
        {
            HexLinkFolder folder = folders[i];
            HexLinkManifest remote = skipped is null ? await peer.Manifest(folder.Name, cancel) : await peer.ProvisionManifest(folder.Name, cancel);
            if (remote.Folder != folder.Name)
                throw HexLinkException.Invalid("A manifest names the wrong workspace folder.");
            var tree = new HexLinkTree(folder, streamLarge: bulk);
            HexLinkManifest local = skipped is null ? tree.Manifest() : tree.ProvisionManifest();
            string prefix = plan.Folders[i].Path;
            if (skipped is not null)
            {
                foreach (string excluded in (remote.ExcludedSystemFiles ?? []).Concat(local.ExcludedSystemFiles ?? []).Distinct(StringComparer.Ordinal))
                    SkipFile(Relative(prefix, excluded), HexLinkSystemFiles.Reason);
                foreach (HexLinkProvisionSkip issue in (remote.ProvisionIssues ?? []).Concat(local.ProvisionIssues ?? []))
                    SkipFile(Relative(prefix, issue.Path), issue.Reason);
                foreach (string path in (local.NestedHeads?.Keys ?? []).Union(remote.NestedHeads?.Keys ?? [], StringComparer.Ordinal))
                {
                    string relative = Relative(prefix, path);
                    if (!HexLinkProvisionPlan.Skips(skipped, relative)
                        && (local.NestedHeads?.ContainsKey(path) != true || remote.NestedHeads?.ContainsKey(path) != true
                            || local.NestedHeads[path] != remote.NestedHeads[path]))
                    {
                        string reason = local.GitIssues?.GetValueOrDefault(path)?.Message
                            ?? remote.GitIssues?.GetValueOrDefault(path)?.Message
                            ?? "The repository's HEAD changed while the workspace was transferred.";
                        skipped.Add(new(relative, reason));
                    }
                }
            }
            HexLinkMissingRepositories? omitted = (skipped ?? plan.Skipped) is { Count: > 0 } skip
                ? new([.. skip.Where(entry => !entry.File && HexLinkProvisionPlan.Inside(entry.Path, prefix))
                    .Select(entry => prefix.Length == 0 ? entry.Path : entry.Path[(prefix.Length + 1)..])], [])
                : null;
            if ((repos is null ? HexLinkTree.CommitsDiffer(local, remote, omitted) : CheckRepositoryHeads(plan, local, remote)) is { } why)
                throw new InvalidOperationException($"The host changed while the workspace was being transferred. {why}. Retry with a new plan.");

            bool includeSystemFiles = folder.IncludeSystemFiles && remote.IncludeSystemFiles;
            bool CopyPath(string within, HexLinkManifest manifest) =>
                omitted?.Blocks(within) != true && !withheldFiles.Contains(Relative(prefix, within))
                && InRepositoryScope(within, manifest.NestedHeads, repos)
                  && (includeSystemFiles || !HexLinkSystemFiles.Matches(within));

            var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localFiles = local.Files.Where(file => CopyPath(UnderFolder(file.Path, folder), local))
                .ToDictionary(file => UnderFolder(file.Path, folder), StringComparer.Ordinal);
            foreach (HexLinkTreeFile file in remote.Files)
            {
                string within = UnderFolder(file.Path, folder);
                if (!CopyPath(within, remote))
                    continue;
                string relative = Relative(prefix, within);
                paths.TryGetValue(relative, out string? oldCase);
                bool added = remotePaths.Add(within);
                try
                {
                    if (!added || oldCase is not null && oldCase != relative)
                    {
                        const string collision = "Paths collide on a case-insensitive checkout.";
                        if (skipped is null)
                            throw HexLinkException.Invalid(collision);
                        SkipFile(oldCase ?? relative, collision);
                        if (oldCase != relative)
                            SkipFile(relative, collision);
                        transfers.Remove(relative);
                        continue;
                    }
                    paths.TryAdd(relative, relative);
                    Resolve(destination, relative, includeSystemFiles);
                    localFiles.TryGetValue(within, out HexLinkTreeFile? here);
                    if (here is not null && (here.Hash is not null && here.Hash == file.Hash
                        || here.Text is not null && here.Text == file.Text
                        || here.Big && file.Big && here.Size == file.Size))
                        continue;
                    if (file.Unavailable)
                        throw new IOException("The host could not read the file.");
                    if (!bulk && (file.Big || file.Size > HexLinkFiles.MOST_READ))
                        throw new InvalidOperationException("The working file exceeds this link's 8 MB transfer limit.");
                    if (file.Hash is null)
                        throw new IOException("The source could not certify the file's contents.");
                    CheckLocal(relative, here?.Hash, here is not null);
                    transfers.TryAdd(relative, new FileTransfer(relative, here?.Hash, file, includeSystemFiles));
                }
                catch (Exception error) when (skipped is not null && ItemFailure(error, peer))
                {
                    SkipFile(relative, error.Message);
                }
            }

            foreach (var (within, here) in localFiles)
            {
                string relative = Relative(prefix, within);
                if (remotePaths.Contains(within) || withheldFiles.Contains(relative))
                    continue;
                try
                {
                    if (!deleted.Contains(relative) && !previous.ContainsKey(relative))
                    {
                        if (dirty.Contains(relative) || !plan.Repositories.Any(repo => HexLinkProvisionPlan.Inside(relative, repo.Path)))
                            throw new InvalidOperationException("Local file is absent on the host. The local copy was kept.");
                        continue;
                    }

                    CheckLocal(relative, here.Hash, exists: true);
                    transfers.TryAdd(relative, new FileTransfer(relative, here.Hash, null, includeSystemFiles));
                }
                catch (Exception error) when (skipped is not null && ItemFailure(error, peer))
                {
                    SkipFile(relative, error.Message);
                }
            }
        }

        // Record all possible writes before the first mutation, so retry can distinguish a partial transfer from local edits.
        foreach (FileTransfer transfer in transfers.Values)
            previous[transfer.Relative] = new JsonObject { ["before"] = transfer.Before, ["after"] = transfer.Remote?.Hash };
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        HexLinkFiles.WriteAtomic(journal, System.Text.Encoding.UTF8.GetBytes(previous.ToJsonString()));

        int done = 0;
        long bytesDone = 0, bytesTotal = transfers.Values.Sum(transfer => transfer.Remote?.Size ?? 0);
        var reporting = new Lock();
        Report();
        await Parallel.ForEachAsync(transfers.Values, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel },
            async (transfer, token) =>
            {
                long copied = 0;
                string? problem = null;
                try
                {
                    copied = await ApplyWorkingFile(peer, destination, transfer, bulk, dirty.Contains(transfer.Relative), token);
                }
                catch (Exception error) when (skipped is not null && ItemFailure(error, peer))
                {
                    problem = error.Message;
                }

                lock (reporting)
                {
                    done++;
                    bytesDone += copied;
                    if (problem is not null)
                        SkipFile(transfer.Relative, problem);
                    Report(problem is not null);
                }
            });

        void CheckLocal(string path, string? hash, bool exists)
        {
            bool touchedByTransfer = previous[path] is JsonObject entry
                && (entry.ContainsKey("before") && HexLinkData.String(entry, "before", 64) == hash
                    || entry.ContainsKey("after") && HexLinkData.String(entry, "after", 64) == hash);
            bool inGit = plan.Repositories.Any(repo => HexLinkProvisionPlan.Inside(path, repo.Path));
            if (exists && hash is null || !touchedByTransfer && (dirty.Contains(path) || exists && !inGit))
                throw new InvalidOperationException($"{path} has local changes. Save them before getting the workspace.");
        }

        void SkipFile(string path, string reason)
        {
            withheldFiles.Add(path);
            skipped!.Add(new(path, reason, File: true));
        }

        void Report(bool includeSkips = true) => progress?.Report(new HexLinkProvisionProgress(HexLinkProvisionPhase.Files, plan.Repositories.Count,
            plan.Repositories.Count, bytesDone, bytesTotal, done, transfers.Count,
            includeSkips && skipped is { Count: > 0 } ? SortedSkips(skipped) : null));
    }

    private static async Task<long> ApplyWorkingFile(HexLinkPeer peer, string destination, FileTransfer transfer, bool bulk,
        bool dirty, CancellationToken cancel)
    {
        var file = transfer.Remote;
        if (!bulk)
        {
            byte[]? content = file is null ? null : await ReadSnapshot(peer, file, cancel);
            string path = Resolve(destination, transfer.Relative, transfer.IncludeSystemFiles);
            if (content is null || transfer.Before is null || dirty || !await MatchesCheckoutText(path, transfer.Before, content, cancel))
                await HexLinkFiles.CompareAndReplace(path, transfer.Relative, transfer.Before, content, cancel, file?.Executable);
            return content?.LongLength ?? 0;
        }

        return await peer.WhileProvisionView(async stop =>
        {
            if (file is null)
            {
                string delete = Resolve(destination, transfer.Relative, transfer.IncludeSystemFiles);
                await HexLinkFiles.CompareAndReplace(delete, transfer.Relative, transfer.Before, null, 0, null, stop);
                return 0L;
            }

            string stage = await peer.ReadProvisionBulk(file, stop);
            try
            {
                string path = Resolve(destination, transfer.Relative, transfer.IncludeSystemFiles);
                if (file.Size <= HexLinkFiles.MOST_READ && transfer.Before is { } expected && !dirty
                    && await MatchesCheckoutText(path, expected, await File.ReadAllBytesAsync(stage, stop), stop))
                    return file.Size;
                await using FileStream input = HexLinkFileTransfer.Read(stage);
                await HexLinkFiles.CompareAndReplace(path, transfer.Relative, transfer.Before, input, file.Size, file.Hash, stop, file.Executable);
                return file.Size;
            }
            finally { HexLinkGit.TryDelete(stage); }
        }, cancel);
    }

    private static async Task<bool> MatchesCheckoutText(string path, string expected, byte[] content, CancellationToken cancel)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, useAsync: true);
        long length = stream.Length;
        if (length > HexLinkFiles.MOST_READ)
            return false;
        byte[] checkout = new byte[(int)length];
        await stream.ReadExactlyAsync(checkout, cancel);
        return stream.ReadByte() == -1 && HexLinkFiles.Hash(checkout) == expected && HexLinkSync.SameText(checkout, content);
    }

    private static string UnderFolder(string wire, HexLinkFolder folder)
    {
        if (!wire.StartsWith(folder.Name + "/", StringComparison.Ordinal) || wire.Length <= folder.Name.Length + 1)
            throw HexLinkException.Invalid("A file in a manifest is outside the workspace folder.");
        return wire[(folder.Name.Length + 1)..];
    }

    private static string Relative(string prefix, string inside) => prefix.Length == 0 ? inside : prefix + "/" + inside;

    private static async Task<byte[]> ReadSnapshot(HexLinkPeer peer, HexLinkTreeFile file, CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                HexLinkFile read = await peer.Read(file.Path, cancel);
                if (read.Hash != file.Hash)
                    throw new IOException($"{file.Path} changed on the host. Retry to get its latest contents.");
                return read.Bytes;
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.BUSY && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancel);
            }
        }
    }

    public static async Task Align(HexLinkPeer peer, HexLinkFolder folder, IProgress<HexLinkProvisionProgress>? progress, CancellationToken cancel)
    {
        progress?.Report(new HexLinkProvisionProgress(HexLinkProvisionPhase.Planning, 0, 0));
        HexLinkProvisionPlan plan = await peer.GetProvisionPlan(folder.Name, cancel);
        if (plan.Folders is not [var remote] || remote.Name != folder.Name || remote.Path.Length != 0 || plan.Repositories.Count == 0)
            throw new InvalidOperationException($"{peer.Device.Name} did not advertise any repositories for {folder.Name}.");
        await ApplyRepositories(peer, plan, folder.Path, align: true, progress, cancel);
    }

    internal static async Task ApplyRepositories(IHexLinkBundleSource source, HexLinkProvisionPlan plan, string destination,
        bool align, IProgress<HexLinkProvisionProgress>? progress, CancellationToken cancel, List<HexLinkProvisionSkip>? skipped = null)
    {
        if (HexLinkGit.Why() is { } why)
            throw new InvalidOperationException(why);

        foreach (HexLinkProvisionFolder folder in plan.Folders)
            Resolve(destination, folder.Path);

        // Parents precede their children on checkout. Bundles are independent and may travel in parallel.
        var checkouts = new List<Checkout>();
        foreach (HexLinkProvisionRepository repository in plan.Repositories.OrderBy(repo => repo.Path.Length))
            TryRepository(repository, () => checkouts.Add(Prepare(plan, destination, repository, align, cancel)));
        var got = new long[checkouts.Count];
        var sizes = new long[checkouts.Count];
        Array.Fill(sizes, -1);
        var reporting = new Lock();
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, checkouts.Count), new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancel },
                async (index, token) =>
                {
                    Checkout checkout = checkouts[index];
                    void Report(long done, long size)
                    {
                        lock (reporting)
                        {
                            got[index] = done;
                            sizes[index] = size;
                            progress?.Report(new HexLinkProvisionProgress(HexLinkProvisionPhase.Repositories, index + 1, checkouts.Count,
                                got.Sum(), sizes.Any(bytes => bytes < 0) ? -1 : sizes.Sum()));
                        }
                    }

                    if (align && checkout.Head == checkout.Remote.Head)
                    {
                        Report(0, 0);
                        return;
                    }
                    Report(0, -1);
                    try
                    {
                        checkout.Bundle = await source.TransferBundle(checkout.Remote.Id, checkout.Have, Report,
                            checkout.Existing ? checkout.Directory : null, token);
                        if (checkout.Bundle is null && !checkout.Existing)
                            throw new IOException("The host returned no bundle for a new checkout.");
                        long total = checkout.Bundle is { } bundle ? new FileInfo(bundle).Length : 0;
                        Report(total, total);
                    }
                    catch (Exception e) when (CanSkip(checkout.Remote, e))
                    {
                        lock (reporting)
                        {
                            skipped!.Add(new(checkout.Remote.Path, e.Message));
                            progress?.Report(new(HexLinkProvisionPhase.Repositories, index + 1, checkouts.Count, Skipped: SortedSkips(skipped)));
                        }
                        Report(got[index], got[index]);
                    }
                });

            // Import first. Reject divergent or dirty checkouts before moving any working tree.
            foreach (Checkout checkout in checkouts)
                TryRepository(checkout.Remote, () => Import(checkout, cancel));
            foreach (Checkout checkout in checkouts)
                TryRepository(checkout.Remote, () => CheckFastForward(plan, checkout, align, cancel));
            foreach (Checkout checkout in checkouts)
                TryRepository(checkout.Remote, () =>
                {
                    if ((checkout.Head != checkout.Remote.Head || checkout.Branch != checkout.Remote.Branch)
                        && (skipped is null || checkout.Existing || !CheckoutSafeSubset(checkout, skipped, cancel)))
                        CheckoutAt(checkout, cancel);
                    if (!align || !checkout.Existing)
                        SetOrigin(checkout, cancel);
                });

            foreach (Checkout checkout in checkouts.Where(item => item.Remote.ParentRepository is not null).Reverse())
                TryRepository(checkout.Remote, () =>
                {
                    string parent = Resolve(destination, checkout.Remote.ParentRepository!);
                    string module = Path.GetRelativePath(parent, checkout.Directory).Replace(Path.DirectorySeparatorChar, '/');
                    Run(parent, ["--literal-pathspecs", "submodule", "init", "--", module], cancel);
                    Run(parent, ["--literal-pathspecs", "submodule", "absorbgitdirs", "--", module], cancel);
                });
        }
        finally
        {
            foreach (Checkout checkout in checkouts)
            {
                if (checkout.Bundle is { } bundle)
                    HexLinkGit.TryDelete(bundle);
            }
        }

        bool CanSkip(HexLinkProvisionRepository repository, Exception error) =>
            skipped is not null && !HexLinkProvisionPlan.RootOf(plan.Folders, repository.Path) && ItemFailure(error, source as HexLinkPeer);

        void TryRepository(HexLinkProvisionRepository repository, Action tryIt)
        {
            cancel.ThrowIfCancellationRequested();
            if (skipped is not null && HexLinkProvisionPlan.Skips(skipped, repository.Path))
                return;
            try
            {
                tryIt();
            }
            catch (Exception error) when (CanSkip(repository, error))
            {
                skipped!.Add(new(repository.Path, error.Message));
                progress?.Report(new(HexLinkProvisionPhase.Repositories, 0, plan.Repositories.Count, Skipped: SortedSkips(skipped)));
            }
        }
    }

    private static Checkout Prepare(HexLinkProvisionPlan plan, string destination, HexLinkProvisionRepository remote, bool align, CancellationToken cancel)
    {
        string directory = Resolve(destination, remote.Path);
        bool existing = HasRepository(directory);
        if (!existing)
        {
            if (align && remote.Path.Length == 0)
                throw new InvalidOperationException($"There is no Git checkout at {directory}.");
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidOperationException($"The destination {directory} already contains files and has no Git checkout.");
            return new Checkout(remote, directory, null, null, []);
        }

        string? head = ReadHead(directory, cancel);
        string? branch = ReadBranch(directory, cancel);
        var checkout = new Checkout(remote, directory, head, branch, HexLinkGit.Haves(directory, cancel)) { Existing = true };
        if (head != remote.Head || branch != remote.Branch)
            RequireClean(plan, checkout, cancel);
        return checkout;
    }

    private static void Import(Checkout checkout, CancellationToken cancel)
    {
        if (!checkout.Existing)
        {
            Directory.CreateDirectory(checkout.Directory);
            Run(checkout.Directory, ["init", "-q", "--object-format=" + (checkout.Remote.Head.Length == 64 ? "sha256" : "sha1"),
                "--initial-branch=hex-link-unborn-" + Guid.NewGuid().ToString("N")], cancel);
            if (OperatingSystem.IsWindows())
                Run(checkout.Directory, ["config", "--local", "core.longpaths", "true"], cancel);
        }

        if (checkout.Bundle is not { } bundle)
            return;
        Run(checkout.Directory, ["bundle", "verify", "-q", bundle], cancel);
        Run(checkout.Directory, ["bundle", "unbundle", bundle], cancel);
        if (checkout.Existing)
            return;

        var updates = new System.Text.StringBuilder();
        foreach (var (name, sha) in HexLinkGit.Refs(bundle, cancel))
        {
            if ((name.StartsWith("refs/heads/", StringComparison.Ordinal) || name.StartsWith("refs/tags/", StringComparison.Ordinal))
                && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                updates.Append("update ").Append(name).Append(' ').Append(sha).Append('\n');
        }
        if (updates.Length > 0)
            Run(checkout.Directory, ["update-ref", "--stdin"], cancel, updates.ToString());
    }

    private static void CheckFastForward(HexLinkProvisionPlan plan, Checkout checkout, bool align, CancellationToken cancel)
    {
        string directory = checkout.Directory, target = checkout.Remote.Head;
        Run(directory, ["cat-file", "-e", target + "^{commit}"], cancel);
        if (checkout.Head == target && checkout.Branch == checkout.Remote.Branch)
            return;

        if (checkout.Existing)
            RequireClean(plan, checkout, cancel);

        if (checkout.Existing && ReadHead(directory, cancel) != checkout.Head)
            throw new InvalidOperationException($"HEAD changed while preparing {directory}. Retry when Git is idle.");
        CheckAncestor(checkout.Head);
        if (checkout.Remote.Branch is { } branch)
        {
            if (branch.StartsWith('-'))
                throw new InvalidOperationException("A workspace branch name cannot start with a dash.");
            Run(directory, ["check-ref-format", "refs/heads/" + branch], cancel);
            if (checkout.Existing)
            {
                GitResult oldBranch = GitProcess.Run(directory, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], cancel: cancel);
                if (oldBranch.Ok)
                    CheckAncestor(oldBranch.Output.Trim());
            }
        }

        void CheckAncestor(string? old)
        {
            if (old is null || old == target)
                return;
            GitResult ancestor = GitProcess.Run(directory, ["merge-base", "--is-ancestor", old, target], cancel: cancel);
            if (ancestor.Exit == 1)
                throw new InvalidOperationException($"{directory} has commits that would be left behind. Merge or save them on another branch before {(align ? "aligning" : "getting the workspace")}.");
            if (!ancestor.Ok)
                throw new InvalidOperationException($"{directory}: {HexLinkGit.ErrorOf(ancestor)}");
        }
    }

    private static void RequireClean(HexLinkProvisionPlan plan, Checkout checkout, CancellationToken cancel)
    {
        string repo = checkout.Remote.Path;
        string prefix = repo.Length > 0 ? repo + "/" : string.Empty;
        string[] children = [.. plan.Repositories
            .Where(child => child.Path != repo && HexLinkProvisionPlan.Inside(child.Path, repo))
            .Select(child => child.Path[prefix.Length..])];
        foreach (string line in Status(checkout.Directory, cancel))
        {
            if (line.StartsWith("?? ", StringComparison.Ordinal)
                && children.Any(child => HasRepository(Path.Combine(checkout.Directory, child)) && HexLinkProvisionPlan.Inside(line[3..].TrimEnd('/'), child)))
                continue;
            throw new InvalidOperationException($"{checkout.Directory} has uncommitted changes. Commit or stash them before aligning to the host.");
        }
    }

    internal static string[] Status(string directory, CancellationToken cancel) =>
        Run(directory, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignore-submodules=all"], cancel)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static void CheckoutAt(Checkout checkout, CancellationToken cancel)
    {
        string directory = checkout.Directory, head = checkout.Remote.Head;
        string[] args = ["checkout", "--quiet", "--no-overwrite-ignore", "--no-recurse-submodules"];
        if (checkout.Remote.Branch is not { } branch)
        {
            Run(directory, [.. args, "--detach", head], cancel);
            return;
        }

        GitResult current = GitProcess.Run(directory, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], cancel: cancel);
        if (current.Exit == 1)
        {
            Run(directory, [.. args, "-b", branch, head], cancel);
            return;
        }
        if (!current.Ok)
            throw new InvalidOperationException($"{directory}: {HexLinkGit.ErrorOf(current)}");

        if (ReadBranch(directory, cancel) != branch || checkout.Head is null)
            Run(directory, [.. args, branch], cancel);
        if (ReadHead(directory, cancel) != head)
            Run(directory, ["-c", "submodule.recurse=false", "merge", "--ff-only", "--no-edit", head], cancel);
    }

    private static void SetOrigin(Checkout checkout, CancellationToken cancel)
    {
        if (checkout.Remote.Origin is not { Length: > 0 } url)
            return;

        string directory = checkout.Directory;
        Run(directory, ["config", "--local", "remote.origin.url", url], cancel);
        Run(directory, ["config", "--local", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"], cancel);
        if (checkout.Remote.Branch is { } branch)
        {
            Run(directory, ["config", "--local", $"branch.{branch}.remote", "origin"], cancel);
            Run(directory, ["config", "--local", $"branch.{branch}.merge", "refs/heads/" + branch], cancel);
        }
    }

    internal static string Resolve(string destination, string relative, bool includeSystemFiles = false)
    {
        const string root = "workspace";
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        RejectLinks(full);
        string wire = relative.Length == 0 ? root : root + "/" + relative;
        if (relative.EndsWith("/", StringComparison.Ordinal))
            throw HexLinkException.Invalid("A path in the workspace plan ends in a slash.");
        string path = HexLinkFiles.Resolve([new HexLinkFolder(root, full) { IncludeSystemFiles = includeSystemFiles }], wire);
        string spelled = Path.GetFullPath(Path.Combine(full, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PlatformPaths.Same(path, spelled))
            throw HexLinkException.Invalid($"A link points elsewhere at {relative}.");
        RejectLinks(spelled);
        return spelled;
    }

    private static void RejectLinks(string path)
    {
        // macOS's standard temp and home aliases are outside the workspace. Check from its root down on each resolve.
        if ((new DirectoryInfo(path)).LinkTarget is not null || (new FileInfo(path)).LinkTarget is not null)
            throw HexLinkException.Invalid($"The destination {path} is a symbolic link or junction.");
    }

    internal static bool HasRepository(string directory) =>
        File.Exists(Path.Combine(directory, ".git")) || Directory.Exists(Path.Combine(directory, ".git"));

    internal static string? ReadHead(string repo, CancellationToken cancel)
    {
        GitResult head = GitProcess.Run(repo, ["rev-parse", "--verify", "--quiet", "HEAD"], cancel: cancel);
        if (head.Exit == 1)
            return null;
        return head.Ok && HexLinkGit.IsCommitId(head.Output.Trim())
            ? head.Output.Trim()
            : throw new InvalidOperationException($"{repo}: {HexLinkGit.ErrorOf(head)}");
    }

    internal static string? ReadBranch(string repo, CancellationToken cancel)
    {
        const string prefix = "refs/heads/";
        GitResult branch = GitProcess.Run(repo, ["symbolic-ref", "--quiet", "HEAD"], cancel: cancel);
        if (branch.Exit == 1)
            return null;
        string full = branch.Output.Trim();
        return branch.Ok && full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..]
            : throw new InvalidOperationException($"{repo}: {HexLinkGit.ErrorOf(branch)}");
    }

    internal static string Run(string directory, string[] args, CancellationToken cancel, string? input = null)
    {
        string[] arguments = OperatingSystem.IsWindows() ? ["-c", "core.longpaths=true", .. args] : args;
        GitResult result = GitProcess.Run(directory, arguments, input, patience: GIT_PATIENCE, unattended: true, cancel: cancel);
        return result.Ok ? result.Output : throw new InvalidOperationException($"{directory}, git {args[0]}: {HexLinkGit.ErrorOf(result)}");
    }
}
