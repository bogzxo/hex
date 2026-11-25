namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>A device and the folder of this workspace it is synced with, with the wire paths it holds in it just now.</summary>
    internal sealed record LinkRemoteHoldSource(string DeviceId, string DeviceName, string FolderName, string FolderRoot, IReadOnlyList<string> Wires);

    /// <summary>The device that holds a file: the id, and the name it is shown as.</summary>
    internal sealed record LinkRemoteHolder(string DeviceId, string Name);

    // Every file a linked peer holds, by its full path here. A new dictionary each time it changes, never changed in place,
    // for the tools' threads read it while the editor's thread replaces it
    private volatile IReadOnlyDictionary<string, LinkRemoteHolder> linkRemoteHolds = new Dictionary<string, LinkRemoteHolder>(PlatformPaths.Comparer);

    /// <summary>
    /// Where each file a peer holds is on this machine: a wire path is its folder's name, then the path in it with '/', so
    /// the folder is found by that name and the rest laid under its root with the platform's separator. A wire of a folder
    /// that isn't the source's (or with nothing after the name) is left out, and when two devices hold the same file the
    /// first of the sources is the one shown.
    /// </summary>
    internal static Dictionary<string, LinkRemoteHolder> LinkRemoteHoldMap(IEnumerable<LinkRemoteHoldSource> sources)
    {
        var map = new Dictionary<string, LinkRemoteHolder>(PlatformPaths.Comparer);
        foreach (LinkRemoteHoldSource source in sources)
        {
            foreach (string wire in source.Wires)
            {
                int slash = wire.IndexOf('/');
                if (slash <= 0 || slash == wire.Length - 1 || !PlatformPaths.Comparer.Equals(wire[..slash], source.FolderName))
                    continue;
                string full = Path.Combine(source.FolderRoot, wire[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar));
                map.TryAdd(full, new LinkRemoteHolder(source.DeviceId, source.DeviceName));
            }
        }

        return map;
    }

    /// <summary>The device that holds this file just now, null when none does: its edits stay on its machine until it releases the file.</summary>
    private LinkRemoteHolder? LinkRemoteHolderOf(string? path) =>
        path is not null && linkRemoteHolds.TryGetValue(path, out LinkRemoteHolder? holder) ? holder : null;

    // On the editor's thread, a sync moved or came or went: what the peers hold made again from every running sync, and when
    // it is not what it was the rows, tabs, the Link window and the editor's notices drawn again
    private void LinkRemoteHoldsRebuild()
    {
        if (linkServices is not { } services)
        {
            LinkRemoteHoldsSet(new Dictionary<string, LinkRemoteHolder>(PlatformPaths.Comparer));
            return;
        }

        var sources = new List<LinkRemoteHoldSource>();
        lock (linkSyncGate)
        {
            foreach (LinkSyncEntry entry in linkSyncs.Values)
            {
                if (entry.Sync is not { } sync || sync.RemoteHeldPaths is not { Count: > 0 } wires)
                    continue;
                HexLinkFolder? local = LinkProvisionedLocal(entry.DeviceId, entry.Folder)
                    ?? services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, entry.Folder));
                if (local is not null)
                    sources.Add(new LinkRemoteHoldSource(entry.DeviceId, LinkNameOf(entry.DeviceId, entry.DeviceName), local.Name, local.Path, wires));
            }
        }

        LinkRemoteHoldsSet(LinkRemoteHoldMap(sources));
    }

    private void LinkRemoteHoldsSet(Dictionary<string, LinkRemoteHolder> made)
    {
        IReadOnlyDictionary<string, LinkRemoteHolder> was = linkRemoteHolds;
        if (was.Count == made.Count && made.All(pair => was.TryGetValue(pair.Key, out LinkRemoteHolder? holder) && holder == pair.Value))
            return;

        linkRemoteHolds = made;
        LinkUpdateExplorerPresence();
        DecorateRows();
        tabsDirty = linkDirty = true;
        MarkLinkEditorNotices();
    }
}
