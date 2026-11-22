using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    // What hears of the changes of each watched tree of the other machine's, by folder
    private readonly ConcurrentDictionary<string, Action<IReadOnlyList<HexLinkTreeChange>>> trees = new(StringComparer.Ordinal);

    /// <summary>
    /// A folder of the other machine's workspace as the working-tree sync sees it: every file git would carry there
    /// (tracked, and untracked but not ignored), with the hash of its bytes, and the commit its repository is at.
    /// </summary>
    public async Task<HexLinkManifest> Manifest(string folder, CancellationToken cancel = default) =>
        await ReadManifest("sync/manifest", folder, cancel);

    /// <summary>
    /// A folder of the other machine's watched: its manifest now, then <paramref name="changes"/> told of every batch of
    /// files that changed there (on the reading task), a short while after they did.
    /// </summary>
    public async Task<HexLinkManifest> WatchTree(string folder, Action<IReadOnlyList<HexLinkTreeChange>> changes, CancellationToken cancel = default)
    {
        trees[folder] = changes;
        try
        {
            HexLinkManifest manifest = await ReadManifest("sync/watch", folder, cancel);
            RememberTree(folder, manifest);
            return manifest;
        }
        catch (Exception)
        {
            trees.TryRemove(folder, out _);
            try { await UnwatchTree(folder); } catch (HexLinkException) { }
            throw;
        }
    }

    internal Task<HexLinkManifest> ProvisionManifest(string folder, CancellationToken cancel) =>
        ReadManifest("sync/manifest", folder, cancel, provision: true);

    private async Task<HexLinkManifest> ReadManifest(string method, string folder, CancellationToken cancel, bool provision = false)
    {
        var patience = TimeSpan.FromMinutes(5);
        JsonObject said = await AskObject(method, new JsonObject { ["folder"] = folder, ["paged"] = true, ["provision"] = provision }, cancel, patience);
        HexLinkManifest first = HexLinkTree.Of(said);
        if (first.Folder != folder)
            throw HexLinkException.Invalid("The peer sent a manifest for a different folder.");
        if (HexLinkData.String(said, "snapshot", 64) is not { } id)
            return first;

        int total = HexLinkData.Int(said, "total") ?? -1;
        HexLinkTree.CheckCount(total);
        if (!first.Certified || HexLinkData.Int(said, "offset") != 0 || first.Files.Count == 0 || total <= first.Files.Count)
            throw HexLinkException.Invalid("The peer sent an invalid first manifest page.");

        JsonNode? repositories = said["repositories"], gitIssues = said["git_issues"];
        var files = new List<HexLinkTreeFile>(total);
        files.AddRange(first.Files);
        var paths = new HashSet<string>(files.Select(file => file.Path), StringComparer.Ordinal);
        while (files.Count < total)
        {
            said = await AskObject(method, new JsonObject { ["folder"] = folder, ["snapshot"] = id, ["offset"] = files.Count }, cancel, patience);
            HexLinkManifest page = HexLinkTree.Of(said);
            if (HexLinkData.String(said, "snapshot", 64) != id || HexLinkData.Int(said, "offset") != files.Count ||
                HexLinkData.Int(said, "total") != total || page.Folder != first.Folder || page.Head != first.Head ||
                page.Root != first.Root || page.CaseSensitive != first.CaseSensitive || page.Checkout != first.Checkout ||
                page.Authority != first.Authority || !JsonNode.DeepEquals(said["repositories"], repositories) || !JsonNode.DeepEquals(said["git_issues"], gitIssues) ||
                !page.Certified || page.Files.Count == 0 || page.Files.Count > total - files.Count)
                throw HexLinkException.Invalid("The peer sent an inconsistent manifest page.");
            foreach (HexLinkTreeFile file in page.Files)
            {
                if (!paths.Add(file.Path))
                    throw HexLinkException.Invalid($"The peer's manifest repeated {file.Path}.");
                files.Add(file);
            }
        }

        return first with { Files = files };
    }

    /// <summary>A folder of the other machine's no longer watched.</summary>
    public Task UnwatchTree(string folder, CancellationToken cancel = default)
    {
        trees.TryRemove(folder, out _);
        treeWatchFiles.TryRemove(folder, out _);
        return AskWithRelay("sync/unwatch", new JsonObject { ["folder"] = folder }, cancel);
    }

    /// <summary>
    /// Done once the other machine has said every change of a watched folder made there so far, so what's been written
    /// there is known here before an agent here is told to go on.
    /// </summary>
    public Task Flush(string folder, CancellationToken cancel = default) =>
        AskWithRelay("sync/flush", new JsonObject { ["folder"] = folder }, cancel, TimeSpan.FromMinutes(5));

    internal async Task RenameCase(string folder, string from, string to, string expected, CancellationToken cancel)
    {
        JsonObject said = await AskObject("sync/manifest", new JsonObject
        {
            ["folder"] = folder, ["rename_from"] = from, ["rename_to"] = to, ["base"] = expected
        }, cancel);
        if (HexLinkData.Bool(said, "renamed") != true)
            throw HexLinkException.Invalid("The peer did not confirm the case-only rename.");
    }

    internal async Task<bool> ConfirmAbsent(string folder, string path, CancellationToken cancel)
    {
        JsonObject said = await AskObject("sync/manifest", new JsonObject { ["folder"] = folder, ["absent"] = path }, cancel);
        return HexLinkData.Bool(said, "absent") == true;
    }

    /// <summary>A file of the other machine's deleted if it's still what was read (its hash), conflict otherwise, not found when it's gone.</summary>
    public Task Delete(string path, string baseHash, CancellationToken cancel = default) =>
        AskWithRelay("fs/delete", new JsonObject { ["path"] = path, ["base"] = baseHash }, cancel);

    // A notification of the working-tree sync's, taken
    private void TreeTold(string method, JsonObject said)
    {
        if (method != "sync/changed" || HexLinkData.String(said, "folder", 200) is not { } folder || !trees.TryGetValue(folder, out var hear))
            return;
        var changes = new List<HexLinkTreeChange>();
        foreach (JsonObject change in HexLinkData.Objects(said, "changes", HexLinkTreeWatch.MOST_BATCH))
        {
            if (HexLinkData.String(change, "path", HexLinkFiles.MOST_PATH) is not { } path || !path.StartsWith(folder + "/", StringComparison.Ordinal))
                continue;
            changes.Add(HexLinkTree.ChangeOf(path, change));
        }

        if (changes.Count > 0)
        {
            RememberTreeChanges(folder, changes);
            hear(changes);
        }
    }
}
