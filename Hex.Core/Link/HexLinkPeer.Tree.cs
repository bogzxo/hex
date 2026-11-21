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
        HexLinkTree.Of(await AskObject("sync/manifest", new JsonObject { ["folder"] = folder }, cancel, TimeSpan.FromMinutes(5)));

    /// <summary>
    /// A folder of the other machine's watched: its manifest now, then <paramref name="changes"/> told of every batch of
    /// files that changed there (on the reading task), a short while after they did.
    /// </summary>
    public async Task<HexLinkManifest> WatchTree(string folder, Action<IReadOnlyList<HexLinkTreeChange>> changes, CancellationToken cancel = default)
    {
        trees[folder] = changes;
        try
        {
            return HexLinkTree.Of(await AskObject("sync/watch", new JsonObject { ["folder"] = folder }, cancel, TimeSpan.FromMinutes(5)));
        }
        catch (Exception)
        {
            trees.TryRemove(folder, out _);
            throw;
        }
    }

    /// <summary>A folder of the other machine's no longer watched.</summary>
    public Task UnwatchTree(string folder, CancellationToken cancel = default)
    {
        trees.TryRemove(folder, out _);
        return session.Ask("sync/unwatch", new JsonObject { ["folder"] = folder }, cancel);
    }

    /// <summary>
    /// Done once the other machine has said every change of a watched folder made there so far, so what's been written
    /// there is known here before an agent here is told to go on.
    /// </summary>
    public Task Flush(string folder, CancellationToken cancel = default) =>
        session.Ask("sync/flush", new JsonObject { ["folder"] = folder }, cancel, TimeSpan.FromMinutes(5));

    /// <summary>A file of the other machine's deleted if it's still what was read (its hash), conflict otherwise, not found when it's gone.</summary>
    public Task Delete(string path, string baseHash, CancellationToken cancel = default) =>
        session.Ask("fs/delete", new JsonObject { ["path"] = path, ["base"] = baseHash }, cancel);

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
            hear(changes);
    }
}
