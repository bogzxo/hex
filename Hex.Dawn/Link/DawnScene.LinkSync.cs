using Hex.Core.Git;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The working tree kept the same on this machine and a linked one (docs/hex-link-plan.md, 5.5): an agent here changes
// the code, the agent on the Mac builds it a moment later, and what it changes comes back the same way. Hex.Core's
// HexLinkSync does the syncing, a folder and a device at a time; this is where one is made, started, stopped, shown and
// resumed. The person turns it on (Link: Sync Folder with a device) or an agent does (hex_link_sync), the choice is kept
// in the settings folder's link folder per device and folder, so the sync starts again by itself when the device links
// again, and stops with the link.
//
// Incoming changes use the on-disk base and keep dirty tabs intact. Conflicts and commit differences are shown
// in the Link window with the actions that settle them.
internal sealed partial class DawnScene
{
    // The file the person's (and the agents') choices of what's synced are kept in, in the settings folder's link folder
    private const string LINK_SYNC_CHOICES = "sync.json";

    // Where the syncs' states are kept, under the link data folder
    private const string LINK_SYNC_STATES = "sync";

    // How long hex_link_sync waits at most for a sync's first comparison or a flush
    private static readonly TimeSpan LINK_SYNC_PATIENCE = TimeSpan.FromMinutes(5);

    // The settings folder's link folder (or a self test's home), where the device's key, the known devices and the sync
    // choices are kept; set as Link starts
    private string linkKeys = string.Empty;

    // The syncs by device and folder, running or not, touched from the tools' threads as well as the editor's
    private readonly Dictionary<string, LinkSyncEntry> linkSyncs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock linkSyncGate = new();

    /// <summary>One sync as this scene keeps it: the device and folder, the engine while there's a link for it, and what's been said of it.</summary>
    private sealed class LinkSyncEntry(string deviceId, string folder)
    {
        public string DeviceId { get; } = deviceId;
        public string Folder { get; } = folder;
        public string DeviceName { get; set; } = string.Empty;
        public HexLinkSync? Sync { get; set; }
        public Task? Starting { get; set; }
        public HexLinkSyncState Said { get; set; } = HexLinkSyncState.Off;
        public HashSet<string> Announced { get; } = new(StringComparer.Ordinal);
        public UINotification? GitCard { get; set; }
    }

    private static string LinkSyncKey(string deviceId, string folder) => deviceId + "\n" + folder;

    /* What the Link window shows */

    /// <summary>The syncs of this Hex, by device and folder: those running and those that stopped (the link went, it failed, it waits on git).</summary>
    internal IReadOnlyList<LinkSyncShown> LinkSyncs
    {
        get
        {
            lock (linkSyncGate)
            {
                return [.. linkSyncs.Values.OrderBy(entry => entry.DeviceName, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Folder, StringComparer.OrdinalIgnoreCase).Select(LinkSyncShownOf)];
            }
        }
    }

    private static LinkSyncShown LinkSyncShownOf(LinkSyncEntry entry)
    {
        HexLinkSync? sync = entry.Sync;
        if (sync is null)
            return new LinkSyncShown(entry.DeviceId, entry.DeviceName, entry.Folder, HexLinkSyncState.Off, $"Sync of {entry.Folder} with {entry.DeviceName} waits for it to link", null, 0, 0, 0, 0, false);
        return new LinkSyncShown(entry.DeviceId, entry.DeviceName, entry.Folder, sync.State, sync.Summary, sync.Why, sync.Sent, sync.Received, sync.Conflicts.Count, sync.Skipped.Count, sync.PullOnly,
            sync.FilesDone, sync.FilesTotal, sync.BytesDone, sync.BytesTotal, sync.Queued, sync.Detail);
    }

    /// <summary>The files in conflict across every sync, each with where the other machine's version was put beside it.</summary>
    internal IReadOnlyList<LinkConflictShown> LinkConflicts
    {
        get
        {
            var all = new List<LinkConflictShown>();
            lock (linkSyncGate)
            {
                foreach (LinkSyncEntry entry in linkSyncs.Values)
                {
                    if (entry.Sync is not { } sync)
                        continue;
                    foreach (HexLinkSyncConflict conflict in sync.Conflicts)
                        all.Add(new LinkConflictShown(entry.DeviceId, entry.DeviceName, entry.Folder, conflict.Path, conflict.Kind, conflict.Copy));
                }
            }

            return all;
        }
    }

    /// <summary>The files a sync leaves alone and why (too big, a name the other side can't take), for the window to list.</summary>
    internal IReadOnlyList<HexLinkSyncSkipped> LinkSyncSkipped(string deviceId, string folder) =>
        LinkSyncOf(deviceId, folder)?.Skipped ?? [];

    /// <summary>The engine of a device's folder's sync, null when there isn't one.</summary>
    private HexLinkSync? LinkSyncOf(string deviceId, string folder)
    {
        lock (linkSyncGate)
            return linkSyncs.TryGetValue(LinkSyncKey(deviceId, folder), out LinkSyncEntry? entry) ? entry.Sync : null;
    }

    /// <summary>The sync a wire path is under (its folder's, with that device), null when there's none.</summary>
    private HexLinkSync? LinkSyncOfPath(string deviceId, string wire) =>
        wire.IndexOf('/') is > 0 and var slash ? LinkSyncOf(deviceId, wire[..slash]) : null;

    /* Starting and stopping */

    /// <summary>
    /// A workspace folder synced with a linked device, from any thread: the engine made (or the one there taken again)
    /// and started, the choice kept so it starts again by itself when the device links again. Done when the first
    /// comparison is, the sync's state then says how it went. Null when it started (or was running), why when it
    /// couldn't (no such device linked, no such folder here or there). <paramref name="force"/> goes on when the two
    /// have different commits checked out.
    /// </summary>
    internal async Task<string?> LinkSyncStart(string deviceId, string folder, bool force = false, bool remember = true, bool automatic = false)
    {
        if (link is not { } links || linkServices is not { } services)
            return Said("link.off");
        if (links.PeerOf(deviceId) is not { } peer || peer.IsClosed)
            return Said("link.sync_not_linked", ("device", LinkKnown.FirstOrDefault(known => known.Device.Id == deviceId)?.Device.Name ?? deviceId));
        if ((LinkProvisionedLocal(deviceId, folder) ?? services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder))) is not { } local)
            return Said("link.sync_no_folder_here", ("folder", folder));
        HexLinkAuthority? authority = services.FolderAuthorities.Of(local);
        HexLinkFolder? remote = peer.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(local.Name, each.Name));
        if (authority is not null || remote?.Authority is not null)
        {
            bool refreshed = await TryRefreshLinkWorkspace(peer).ConfigureAwait(false);
            remote = peer.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(local.Name, each.Name));
            if (!refreshed || LinkBoundSyncOwner(services.Device.Id, authority, remote?.Authority) is false)
                return Said("link.sync_authority_required", ("folder", local.Name));
        }

        Task starting;
        LinkSyncEntry entry;
        lock (linkSyncGate)
        {
            string key = LinkSyncKey(peer.Device.Id, local.Name);
            if (!linkSyncs.TryGetValue(key, out LinkSyncEntry? found))
                linkSyncs[key] = found = new LinkSyncEntry(peer.Device.Id, local.Name);
            entry = found;
            entry.DeviceName = peer.Device.Name;

            if (entry.Starting is { IsCompleted: false } already)
            {
                starting = already;
            }
            else if (entry.Sync is { } running && LinkSyncSamePeer(running, peer)
                && running.State is HexLinkSyncState.Syncing or HexLinkSyncState.Idle or HexLinkSyncState.PullOnly or HexLinkSyncState.Comparing)
            {
                starting = Task.CompletedTask;
            }
            else
            {
                entry.Sync?.Dispose();
                var sync = new HexLinkSync(peer, local with { Authority = authority }, Path.Combine(linkData, LINK_SYNC_STATES))
                {
                    ApplyLocal = LinkCompareAndReplace,
                    ApplyStream = LinkCompareAndReplaceStream,
                    RenameLocal = LinkRenameCase,
                    Excludes = () => services.Excludes
                };
                sync.Changed = () => PostLink(() => LinkSyncMoved(entry));
                entry.Sync = sync;
                entry.Announced.Clear();
                entry.Said = HexLinkSyncState.Off;
                starting = entry.Starting = Task.Run(() => sync.Start(force, automatic: automatic));
            }
        }

        if (remember)
            LinkSyncRemember(peer.Device.Id, local.Name, on: true);
        await starting;
        linkDirty = true;
        LinkMoved();
        return null;
    }

    // Whether a sync is of this link of a device (a link made again makes a peer of its own)
    private static bool LinkSyncSamePeer(HexLinkSync sync, HexLinkPeer peer) => sync.Device.Id == peer.Device.Id && !peer.IsClosed
        && sync.State is not HexLinkSyncState.Off and not HexLinkSyncState.Failed;

    /// <summary>A device's folder no longer synced, by the person or an agent, and not started again when it links. False when it wasn't.</summary>
    internal bool LinkSyncStop(string deviceId, string folder)
    {
        LinkSyncEntry? entry;
        lock (linkSyncGate)
        {
            string key = LinkSyncKey(deviceId, folder);
            if (linkSyncs.Remove(key, out entry))
                entry.Sync?.Dispose();
        }

        LinkSyncRemember(deviceId, folder, on: false);
        if (entry?.GitCard is { } card)
            PostLink(card.Close);
        LinkMoved();
        return entry is not null;
    }

    // A device linked: the folders the person chose to sync with it started again
    private void LinkSyncResume(HexLinkPeer peer)
    {
        foreach (string folder in LinkSyncChosen(peer.Device.Id))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await LinkSyncStart(peer.Device.Id, folder, remember: false) is { } why)
                        PostLink(() => Say(Said("link.sync_not_resumed", ("folder", folder), ("device", peer.Device.Name), ("why", why))));
                }
                catch (Exception e)
                {
                    PostLink(() => Say(Said("link.sync_not_resumed", ("folder", folder), ("device", peer.Device.Name), ("why", e.Message)), error: true));
                }
            });
        }
    }

    // Link going down: every sync stopped, its state kept, the choices left as they are for next time
    private void LinkSyncStopAll()
    {
        lock (linkSyncGate)
        {
            foreach (LinkSyncEntry entry in linkSyncs.Values)
            {
                entry.Sync?.Dispose();
                entry.GitCard?.Close();
            }

            linkSyncs.Clear();
        }
    }

    /* The choices kept */

    // The folders chosen to be synced with a device
    private List<string> LinkSyncChosen(string deviceId) => LinkSyncChosenIn(LinkSyncChoices(), deviceId);

    /// <summary>The folders a list of kept choices says are synced with a device.</summary>
    internal static List<string> LinkSyncChosenIn(IEnumerable<JsonObject> choices, string deviceId)
    {
        var chosen = new List<string>();
        foreach (JsonObject each in choices)
        {
            if (HexLinkData.String(each, "device", 200) == deviceId && HexLinkData.Bool(each, "enabled") != false
                && HexLinkData.String(each, "folder", 200) is { Length: > 0 } folder)
                chosen.Add(folder);
        }

        return chosen;
    }

    private List<JsonObject> LinkSyncChoices()
    {
        string file = Path.Combine(linkKeys, LINK_SYNC_CHOICES);
        try
        {
            if (linkKeys.Length > 0 && File.Exists(file) && JsonNode.Parse(File.ReadAllBytes(file)) is JsonObject kept)
                return [.. HexLinkData.Objects(kept, "syncs", 500).Select(each => (JsonObject)each.DeepClone())];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }

        return [];
    }

    // A device's folder kept as synced, or no longer
    private void LinkSyncRemember(string deviceId, string folder, bool on)
    {
        if (linkKeys.Length == 0)
            return;
        lock (linkSyncGate)
        {
            JsonObject kept = LinkSyncChoicesWith(LinkSyncChoices(), deviceId, folder, on, keepDisabled: true);
            try
            {
                Directory.CreateDirectory(linkKeys);
                HexLinkFiles.WriteAtomic(Path.Combine(linkKeys, LINK_SYNC_CHOICES), Encoding.UTF8.GetBytes(kept.ToJsonString()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[Hex] Link couldn't keep which folders are synced, {e.Message}");
            }
        }
    }

    /// <summary>The kept choices with a device's folder turned on or off, once at most, as the file keeps them.</summary>
    internal static JsonObject LinkSyncChoicesWith(List<JsonObject> choices, string deviceId, string folder, bool on, bool keepDisabled = false)
    {
        choices.RemoveAll(each => HexLinkData.String(each, "device", 200) == deviceId && PlatformPaths.Comparer.Equals(HexLinkData.String(each, "folder", 200) ?? string.Empty, folder));
        if (on || keepDisabled)
            choices.Add(new JsonObject { ["device"] = deviceId, ["folder"] = folder, ["enabled"] = on });
        return new JsonObject { ["syncs"] = new JsonArray([.. choices.Select(each => (JsonNode)each)]) };
    }

    /* Writing here what came from there */

    /// <summary>Writes a file from Link and refreshes a clean editor tab with the incoming text.</summary>
    internal async Task LinkWriteLocal(string fullPath, byte[] bytes)
    {
        LinkTabStamp before = await OnEditorForLink(() => LinkTabBeforeWrite(fullPath));
        if (before.Dirty)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{fullPath} has unsaved changes in the editor.");
        await Task.Run(() => HexLinkFiles.WriteAtomic(fullPath, bytes));
        if (LinkText(bytes) is not { } text)
            return;
        await OnEditorForLink(() =>
        {
            LinkTakeIntoTab(fullPath, text, before);
            return true;
        });
    }

    internal async Task LinkRenameCase(string from, string to, string expected, CancellationToken cancel)
    {
        if (await OnEditorForLink(() => Context.Documents.Find(from) is { } open && HasChanges(open)).WaitAsync(cancel))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The file has unsaved changes in the editor.",
                new JsonObject { ["editor_dirty"] = true });
        await Task.Run(() => HexLinkTree.RenameCase(from, to, expected), cancel);
    }

    private readonly record struct LinkTabStamp(HexDocument? Document, string? DiskText, bool Dirty);

    private LinkTabStamp LinkTabBeforeWrite(string full)
    {
        HexDocument? open = Context.Documents.Find(full);
        return new LinkTabStamp(open, open?.DiskText, open is not null && HasChanges(open));
    }

    // A queued editor refresh may arrive after a save, reload or close. Keep the newer document state in that case.
    private bool LinkTakeIntoTab(string full, string text, LinkTabStamp before)
    {
        if (before.Document is not { } open || !ReferenceEquals(Context.Documents.Find(full), open) ||
            open.DiskText != before.DiskText || open.Picture is not null || !open.IsText || text == open.DiskText)
            return false;
        if (HasChanges(open))
            return true;
        TakeFromDisk(open, text);
        return false;
    }

    /* What's said of a sync as it moves */

    // On the editor's thread, a sync moved: a card when it waits on git or a file came into conflict, a line when it failed
    private void LinkSyncMoved(LinkSyncEntry entry)
    {
        linkDirty = true;
        if (entry.Sync is not { } sync)
            return;
        lock (linkSyncGate)
        {
            if (!linkSyncs.TryGetValue(LinkSyncKey(entry.DeviceId, entry.Folder), out LinkSyncEntry? kept) || kept != entry)
                return;
        }

        HexLinkSyncState state = sync.State;
        if (state != entry.Said)
        {
            entry.Said = state;
            if (state == HexLinkSyncState.NeedsGit)
                LinkSyncAskGit(entry, sync);
            else if (state == HexLinkSyncState.Failed)
                Say(Said("link.sync_failed", ("folder", entry.Folder), ("device", entry.DeviceName), ("why", sync.Why ?? string.Empty)), error: true);
            else if (state == HexLinkSyncState.PullOnly)
                Say(Said("link.sync_pull_only", ("folder", entry.Folder), ("device", entry.DeviceName)));
            else if (state is HexLinkSyncState.Syncing or HexLinkSyncState.Idle && entry.GitCard is { } card)
            {
                card.Close();
                entry.GitCard = null;
            }
        }

        foreach (HexLinkSyncConflict conflict in sync.Conflicts)
        {
            if (entry.Announced.Add(conflict.Path + "\n" + conflict.Copy))
                LinkSyncAnnounceConflict(entry, conflict);
        }

        // A conflict resolved (here or by both sides coming to the same thing) is said again if it comes back
        entry.Announced.RemoveWhere(said => !sync.Conflicts.Any(conflict => conflict.Path + "\n" + conflict.Copy == said));
    }

    // The two sides have different commits checked out. Offer a safe fast-forward to the host.
    private void LinkSyncAskGit(LinkSyncEntry entry, HexLinkSync sync)
    {
        entry.GitCard?.Close();
        string device = entry.DeviceName, folder = entry.Folder, id = entry.DeviceId;
        entry.GitCard = toasts.Show(Said("link.sync_needs_git", ("folder", folder), ("device", device), ("why", sync.Why ?? string.Empty)), "warning", null, 0.0f,
            (Said("link.provision.align_button", ("device", device)), () =>
            {
                entry.GitCard?.Close();
                entry.GitCard = null;
                LinkAlignWorkspaceCommand(id, folder);
            }),
            (Said("link.sync_anyway"), () =>
            {
                entry.GitCard?.Close();
                entry.GitCard = null;
                _ = LinkSyncStart(id, folder, force: true);
            }));
    }

    // A file both sides changed: a card naming it and the copy of theirs beside it, to look at the two or pick a side
    private void LinkSyncAnnounceConflict(LinkSyncEntry entry, HexLinkSyncConflict conflict)
    {
        string id = entry.DeviceId, path = conflict.Path;
        string text = conflict.Copy is { } copy
            ? Said("link.sync_conflict", ("path", path), ("device", entry.DeviceName), ("copy", copy[(copy.LastIndexOf('/') + 1)..]), ("kind", conflict.Kind))
            : Said("link.sync_conflict_deleted", ("path", path), ("device", entry.DeviceName), ("kind", conflict.Kind));
        UINotification? card = null;
        var buttons = new List<(string Label, Action Pressed)>();
        if (conflict.Copy is not null)
            buttons.Add((Said("link.sync_open_difference"), () => LinkOpenConflictDifference(id, path)));
        buttons.Add((Said("link.sync_keep_mine"), () =>
        {
            card?.Close();
            LinkResolveSaying(id, path, HexLinkSyncKeep.Mine);
        }));
        buttons.Add((Said("link.sync_keep_theirs", ("device", entry.DeviceName)), () =>
        {
            card?.Close();
            LinkResolveSaying(id, path, HexLinkSyncKeep.Theirs);
        }));
        card = toasts.Show(text, "warning", null, 0.0f, [.. buttons]);
        Note(text, error: false);
    }

    /* Conflicts */

    /// <summary>
    /// A file in conflict settled, this machine's version sent over the other's (mine) or the other's written here
    /// (theirs), the copy beside it gone. Null when it's done, why when it isn't. From any thread.
    /// </summary>
    internal async Task<string?> LinkResolve(string deviceId, string path, HexLinkSyncKeep keep)
    {
        if (LinkSyncOfPath(deviceId, path) is not { } sync)
            return Said("link.sync_none_for", ("path", path));
        string? why = await sync.Resolve(path, keep);
        linkDirty = true;
        LinkMoved();
        return why;
    }

    // Resolved from a card, what came of it said
    private void LinkResolveSaying(string deviceId, string path, HexLinkSyncKeep keep) => _ = Task.Run(async () =>
    {
        string? why = await LinkResolve(deviceId, path, keep);
        PostLink(() =>
        {
            if (why is null)
                Say(Said(keep == HexLinkSyncKeep.Mine ? "link.sync_resolved_mine" : "link.sync_resolved_theirs", ("path", path)));
            else
                Say(why, error: true);
        });
    });

    /// <summary>
    /// A file in conflict and the other machine's version of it put beside it, in Hex's diff, theirs taken out and
    /// mine put in. On the editor's thread. False when there's no such conflict or no copy to look at.
    /// </summary>
    internal bool LinkOpenConflictDifference(string deviceId, string path)
    {
        if (LinkSyncOfPath(deviceId, path) is not { } sync || sync.Conflicts.FirstOrDefault(each => each.Path == path) is not { Copy: { } copy }
            || linkServices?.Workspace.Folders is not { } folders)
            return false;
        try
        {
            string mine = HexLinkFiles.Resolve(folders, path), theirs = HexLinkFiles.Resolve(folders, copy);
            string mineText = Context.Documents.Find(mine) is { IsText: true } open && HasChanges(open)
                ? open.Editor.Text
                : File.Exists(mine) ? File.ReadAllText(mine) : string.Empty;
            string theirText = File.Exists(theirs) ? File.ReadAllText(theirs) : string.Empty;
            OpenDiff(mine, "link", Said("link.sync_difference_title", ("name", Path.GetFileName(mine)), ("device", sync.Device.Name)), GitDiffText.Of(theirText, mineText), -1);
            return true;
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException)
        {
            Say(e.Message, error: true);
            return false;
        }
    }
}

/// <summary>
/// A sync as the Link window shows it: the device and folder, where it is, its line, why it's stopped or pull-only,
/// how many files went each way, how many are in conflict and how many don't travel.
/// </summary>
internal sealed record LinkSyncShown(string DeviceId, string DeviceName, string Folder, HexLinkSyncState State, string Summary, string? Why,
    int Sent, int Received, int Conflicts, int Skipped, bool PullOnly,
    int FilesDone = 0, int FilesTotal = 0, long BytesDone = 0, long BytesTotal = 0, int Queued = 0, string? Detail = null);

/// <summary>A file in conflict as the Link window lists it, the device and folder it's synced with, its wire path, what happened and the copy of theirs beside it.</summary>
internal sealed record LinkConflictShown(string DeviceId, string DeviceName, string Folder, string Path, string Kind, string? Copy);
