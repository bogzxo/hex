using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    /// <summary>The most folders one device may watch the trees of at once.</summary>
    public const int MOST_TREES = 8;

    // The trees of the workspace's folders, kept for their hashes, and those the device watches
    private readonly ConcurrentDictionary<string, HexLinkTree> trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HexLinkTreeSubscription> treeWatches = new(StringComparer.Ordinal);

    /// <summary>How long after the last change in a watched tree its batch goes, 150 ms unless a test says.</summary>
    public TimeSpan TreeQuiet { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How often a watched tree is looked over whole in case its watcher dropped something, 15 seconds unless a test says.</summary>
    public TimeSpan TreeRescan { get; init; } = TimeSpan.FromSeconds(15);

    private readonly Lock paging = new();
    private readonly Dictionary<(string Folder, bool Watch), ManifestSnapshot> manifests = [];
    private sealed record ManifestSnapshot(string Id, HexLinkManifest Manifest, long Expires);

    private JsonObject FirstPage(JsonObject parameters, HexLinkManifest manifest, bool watch)
    {
        bool paged = HexLinkData.Bool(parameters, "paged") == true;
        HexLinkAuthority? authority = Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, manifest.Folder))?.Authority;
        manifest = manifest with { Authority = authority };
        int fullCount = manifest.Files.Count;
        var (changed, equal) = HexLinkDirectoryDigests.Select(manifest, parameters);
        manifest = changed;
        int end = PageEnd(manifest, 0);
        if (end == manifest.Files.Count)
            return WithDirectories(HexLinkTree.Json(manifest));
        if (!paged)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, "The manifest requires a peer that can read it in pages.");

        var snapshot = new ManifestSnapshot(Guid.NewGuid().ToString("N"), manifest, Environment.TickCount64 + 300_000);
        lock (paging)
        {
            foreach (var expired in manifests.Where(each => each.Value.Expires <= Environment.TickCount64).Select(each => each.Key).ToArray())
                manifests.Remove(expired);
            var key = (manifest.Folder, watch);
            if (manifests.Count >= MOST_TREES * 2 && !manifests.ContainsKey(key))
                throw new HexLinkException(HexLinkErrors.BUSY, "Too many manifests are being read at once.");
            manifests[key] = snapshot;
        }

        return WithDirectories(ManifestPage(snapshot, 0, end));

        JsonObject WithDirectories(JsonObject page)
        {
            if (equal is not null)
            {
                page["equal_directories"] = equal;
                page["full_count"] = fullCount;
            }
            return page;
        }
    }

    private JsonObject ContinueManifest(HexLinkTree tree, JsonObject parameters, string id, bool watch)
    {
        var key = (tree.Folder.Name, watch);
        ManifestSnapshot snapshot;
        int start = HexLinkData.Int(parameters, "offset") ?? -1;
        int end;
        lock (paging)
        {
            if (!manifests.TryGetValue(key, out ManifestSnapshot? found) || found.Id != id || found.Expires <= Environment.TickCount64)
                throw HexLinkException.NotFound("The manifest snapshot expired.");
            snapshot = found;
            if (start <= 0 || start >= snapshot.Manifest.Files.Count)
                throw HexLinkException.Invalid("Invalid manifest page offset.");
            end = PageEnd(snapshot.Manifest, start);
            if (end == snapshot.Manifest.Files.Count)
                manifests.Remove(key);
        }

        return ManifestPage(snapshot, start, end);
    }

    private static int PageEnd(HexLinkManifest manifest, int start)
    {
        int end = start, bytes = 0;
        while (end < manifest.Files.Count && (bytes += HexLinkTree.WireCost(manifest.Files[end].Path)) <= HexLinkTree.PAGE_BUDGET)
            end++;
        return end;
    }

    private static JsonObject ManifestPage(ManifestSnapshot snapshot, int start, int end)
    {
        JsonObject page = HexLinkTree.Json(snapshot.Manifest, start, end);
        page["snapshot"] = snapshot.Id;
        page["offset"] = start;
        page["total"] = snapshot.Manifest.Files.Count;
        return page;
    }

    // A folder of the workspace by name, its tree made once (a folder renamed or moved gets a new one)
    private HexLinkTree TreeOf(JsonObject parameters)
    {
        string name = HexLinkData.Need(parameters, "folder", 200);
        HexLinkFolder folder = Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
            ?? throw HexLinkException.NotFound($"The workspace has no folder called {name}.");
        // Through the jail like any path, so a folder that's itself a link out is refused the same way
        HexLinkFiles.Resolve(Folders, folder.Name);
        HexLinkTree tree = trees.GetOrAdd(folder.Name, _ => HexLinkTree.Shared(folder, () => services.Excludes, services.TreeCacheDirectory, session.SupportsBulk));
        if (tree.Folder != folder)
            trees[folder.Name] = tree = HexLinkTree.Shared(folder, () => services.Excludes, services.TreeCacheDirectory, session.SupportsBulk);
        return tree;
    }

    private string ResolveUsableTreeFile(string wire)
    {
        string full = HexLinkFiles.Resolve(Folders, wire, services.Excludes);
        int slash = wire.IndexOf('/');
        if (slash > 0)
        {
            string name = wire[..slash];
            if (!trees.TryGetValue(name, out HexLinkTree? tree))
                tree = trees.Values.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Folder.Name, name));
            if (tree is not null && (tree.RepositoryUnavailable(wire[(slash + 1)..].TrimEnd('/')) ||
                tree.RepositoryUnavailable(Path.GetRelativePath(tree.Root, full).Replace('\\', '/'))))
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Git could not list the repository containing this path.");
        }
        return full;
    }

    private async Task<JsonNode?> SyncManifest(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/manifest");
        HexLinkTree tree = TreeOf(parameters);
        if (HexLinkData.String(parameters, "rename_from", HexLinkFiles.MOST_PATH) is { } from)
        {
            Need(HexLinkRights.Edit, "sync/rename");
            string to = HexLinkData.Need(parameters, "rename_to", HexLinkFiles.MOST_PATH);
            string expected = HexLinkData.Need(parameters, "base", 64);
            string prefix = tree.Folder.Name + "/";
            if (!from.StartsWith(prefix, StringComparison.Ordinal) || !to.StartsWith(prefix, StringComparison.Ordinal) || !HexLinkTree.OnlyCase(from, to))
                throw HexLinkException.Invalid("Both names must be case variants of one file in the folder.");
            if (tree.Ignored([from[prefix.Length..], to[prefix.Length..]]).Count != 0)
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Link cannot rename an ignored file.");
            await services.RenameCase(ResolveFile(from), ResolveFile(to), expected, cancel);
            tree.NoteCase(to[prefix.Length..]);
            return new JsonObject { ["renamed"] = true };
        }
        if (HexLinkData.String(parameters, "absent", HexLinkFiles.MOST_PATH) is { } path)
        {
            string prefix = tree.Folder.Name + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                throw HexLinkException.Invalid("The path is outside the manifest folder.");
            HexLinkFiles.Resolve(Folders, path, services.Excludes);
            return new JsonObject { ["absent"] = tree.Gone(path[prefix.Length..]) };
        }
        if (HexLinkData.String(parameters, "snapshot", 64) is { } id)
            return ContinueManifest(tree, parameters, id, watch: false);
        bool provision = HexLinkData.Bool(parameters, "provision") == true;
        return FirstPage(parameters, await Task.Run(() => provision ? tree.ProvisionManifest() : tree.Manifest(), cancel), watch: false);
    }

    // The tree as it is now and what changes of it from now on, sent as sync/changed
    private async Task<JsonNode?> SyncWatch(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/watch");
        HexLinkTree tree = TreeOf(parameters);
        if (HexLinkData.String(parameters, "snapshot", 64) is { } id)
            return ContinueManifest(tree, parameters, id, watch: true);
        if (!treeWatches.ContainsKey(tree.Folder.Name) && treeWatches.Count >= MOST_TREES)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A device may watch {MOST_TREES} folders at once.");

        string folder = tree.Folder.Name;
        TimeSpan quiet = session.SupportsBulk && TreeQuiet == TimeSpan.FromMilliseconds(150) ? TimeSpan.FromMilliseconds(120) : TreeQuiet;
        var watch = new HexLinkTreeSubscription(tree, changes => TreeMoved(folder, changes), quiet, TreeRescan);
        try
        {
            HexLinkManifest manifest = await Task.Run(tree.Manifest, cancel);
            JsonObject answer = FirstPage(parameters, manifest, watch: true);
            if (treeWatches.TryGetValue(folder, out HexLinkTreeSubscription? was))
                was.Dispose();
            treeWatches[folder] = watch;
            watch.Seed(manifest);
            return answer;
        }
        catch
        {
            watch.Dispose();
            throw;
        }
    }

    private JsonObject SyncUnwatch(JsonObject parameters)
    {
        string name = HexLinkData.Need(parameters, "folder", 200);
        lock (paging)
            manifests.Remove((name, true));
        if (treeWatches.TryRemove(name, out HexLinkTreeSubscription? watch))
            watch.Dispose();
        return [];
    }

    // Answered once every change of the folder made so far has been said
    private async Task<JsonNode?> SyncFlush(JsonObject parameters)
    {
        Need(HexLinkRights.View, "sync/flush");
        string name = HexLinkData.Need(parameters, "folder", 200);
        if (!treeWatches.TryGetValue(name, out HexLinkTreeSubscription? watch))
            throw HexLinkException.NotFound($"{name} isn't watched, sync/watch it first.");
        await watch.Flush();
        return new JsonObject { ["folder"] = name };
    }

    private void TreeMoved(string folder, IReadOnlyList<HexLinkTreeChange> changes)
    {
        using var timing = HexLinkSyncTiming.Measure("watch.send-to-peer", changes.Count);
        if (!HexLinkAllowed.Has(Rights, HexLinkRights.View))
            return;
        var said = new JsonArray();
        foreach (HexLinkTreeChange change in changes)
            said.Add((JsonNode)HexLinkTree.ChangeJson(change));

        session.Tell("sync/changed", new JsonObject { ["folder"] = folder, ["changes"] = said });
    }

    private void StopTrees()
    {
        foreach (var (_, watch) in treeWatches)
            watch.Dispose();
        treeWatches.Clear();
        lock (paging)
            manifests.Clear();
    }
}
