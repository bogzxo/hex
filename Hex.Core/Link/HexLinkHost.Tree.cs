using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    /// <summary>The most folders one device may watch the trees of at once.</summary>
    public const int MOST_TREES = 8;

    // The trees of the workspace's folders, kept for their hashes, and those the device watches
    private readonly ConcurrentDictionary<string, HexLinkTree> trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HexLinkTreeWatch> treeWatches = new(StringComparer.Ordinal);

    /// <summary>How long after the last change in a watched tree its batch goes, 150 ms unless a test says.</summary>
    public TimeSpan TreeQuiet { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How often a watched tree is looked over whole in case its watcher dropped something, 15 seconds unless a test says.</summary>
    public TimeSpan TreeRescan { get; init; } = TimeSpan.FromSeconds(15);

    // A folder of the workspace by name, its tree made once (a folder renamed or moved gets a new one)
    private HexLinkTree TreeOf(JsonObject parameters)
    {
        string name = HexLinkData.Need(parameters, "folder", 200);
        HexLinkFolder folder = Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
            ?? throw HexLinkException.NotFound($"The workspace has no folder called {name}.");
        // Through the jail like any path, so a folder that's itself a link out is refused the same way
        HexLinkFiles.Resolve(Folders, folder.Name);
        HexLinkTree tree = trees.GetOrAdd(folder.Name, _ => new HexLinkTree(folder));
        if (tree.Folder != folder)
            trees[folder.Name] = tree = new HexLinkTree(folder);
        return tree;
    }

    private async Task<JsonNode?> SyncManifest(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/manifest");
        HexLinkTree tree = TreeOf(parameters);
        return HexLinkTree.Json(await Task.Run(tree.Manifest, cancel));
    }

    // The tree as it is now and what changes of it from now on, sent as sync/changed
    private async Task<JsonNode?> SyncWatch(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/watch");
        HexLinkTree tree = TreeOf(parameters);
        if (!treeWatches.ContainsKey(tree.Folder.Name) && treeWatches.Count >= MOST_TREES)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A device may watch {MOST_TREES} folders at once.");

        HexLinkManifest manifest = await Task.Run(tree.Manifest, cancel);
        string folder = tree.Folder.Name;
        var watch = new HexLinkTreeWatch(tree, manifest, changes => TreeMoved(folder, changes), TreeQuiet, TreeRescan);
        if (treeWatches.TryGetValue(folder, out HexLinkTreeWatch? was))
            was.Dispose();
        treeWatches[folder] = watch;
        return HexLinkTree.Json(manifest);
    }

    private JsonObject SyncUnwatch(JsonObject parameters)
    {
        string name = HexLinkData.Need(parameters, "folder", 200);
        if (treeWatches.TryRemove(name, out HexLinkTreeWatch? watch))
            watch.Dispose();
        return [];
    }

    // Answered once every change of the folder made so far has been said
    private async Task<JsonNode?> SyncFlush(JsonObject parameters)
    {
        Need(HexLinkRights.View, "sync/flush");
        string name = HexLinkData.Need(parameters, "folder", 200);
        if (!treeWatches.TryGetValue(name, out HexLinkTreeWatch? watch))
            throw HexLinkException.NotFound($"{name} isn't watched, sync/watch it first.");
        await watch.Flush();
        return new JsonObject { ["folder"] = name };
    }

    private void TreeMoved(string folder, IReadOnlyList<HexLinkTreeChange> changes)
    {
        if (!HexLinkAllowed.Has(Rights, HexLinkRights.View))
            return;
        var said = new JsonArray();
        foreach (HexLinkTreeChange change in changes)
            said.Add((JsonNode)HexLinkTree.ChangeJson(change));

        session.Tell("sync/changed", new JsonObject { ["folder"] = folder, ["changes"] = said });
    }

    // A file deleted if it's still what the deleter read
    private async Task<JsonNode?> FsDelete(JsonObject parameters)
    {
        Need(HexLinkRights.Edit, "fs/delete");
        string wire = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string full = HexLinkFiles.Resolve(Folders, wire);
        string baseHash = HexLinkData.Need(parameters, "base", 64);
        services.Audit("edit", Device, $"fs/delete {wire}");

        await writing.WaitAsync();
        try
        {
            if (!File.Exists(full))
                throw HexLinkException.NotFound($"There's no file {wire}.");
            string? now = HexLinkFiles.HashOf(full);
            if (!string.Equals(now, baseHash, StringComparison.OrdinalIgnoreCase))
                throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} changed since it was read.", new JsonObject { ["hash"] = now });
            await services.DeleteFile(full);
            return new JsonObject { ["path"] = wire };
        }
        finally
        {
            writing.Release();
        }
    }

    private void StopTrees()
    {
        foreach (var (_, watch) in treeWatches)
            watch.Dispose();
        treeWatches.Clear();
    }
}
