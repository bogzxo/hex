namespace Hex.Dawn;

// What's shared with a linked device besides its agents, taken up as it links and let go of as Link goes down, and its
// commands in the palette: syncing a folder with it, opening one of its files, following its person, and its
// repository cloned, fetched or offered changes to.
internal sealed partial class DawnScene
{
    // A device linked, on the editor's thread: what was shared with it before taken up again
    private void LinkShareJoined(HexLinkPeer peer)
    {
        if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            _ = LinkPreparePublicationAndShare(peer);
        LinkFilesJoined(peer);
        CurrentLinkPublication?.Pump.Poke();
        LinkPresenceJoined(peer);
    }

    // One engine compares and watches both sides. If either side can push, it owns the automatic sync.
    internal static bool LinkAutoSyncOwner(bool dialled, HexLinkRights localGrants, HexLinkRights remoteGrants)
    {
        if (!HexLinkAllowed.Has(remoteGrants, HexLinkRights.View))
            return false;
        if (!HexLinkAllowed.Has(localGrants, HexLinkRights.View))
            return true;

        bool canPush = HexLinkAllowed.Has(remoteGrants, HexLinkRights.Edit);
        bool otherCanPush = HexLinkAllowed.Has(localGrants, HexLinkRights.Edit);
        return canPush != otherCanPush ? canPush : !dialled;
    }

    // A null result leaves the pair's election in force. A bound pair needs the same pinned epoch on both ends.
    internal static bool? LinkBoundSyncOwner(string self, HexLinkAuthority? local, HexLinkAuthority? remote)
    {
        if (local is null && remote is null)
            return null;

        return local is not null && remote is not null && local.Epoch == remote.Epoch
            && local.DeviceId.Equals(remote.DeviceId, StringComparison.OrdinalIgnoreCase)
            && local.DeviceId.Equals(self, StringComparison.OrdinalIgnoreCase);
    }

    // A matching workspace folder starts on connect unless the person turned it off. The sync still checks Git identity.
    private void LinkSyncStartSharedFolders(HexLinkPeer peer)
    {
        if (linkServices is not { } services)
            return;

        bool elected = LinkAutoSyncOwner(peer.Session.Connection.Dialled, peer.Session.Connection.Rights, peer.Granted);
        string[] common = [.. services.Workspace.Folders
            .Where(local => peer.Workspace.Folders.Any(remote => PlatformPaths.Comparer.Equals(local.Name, remote.Name)
                && (LinkBoundSyncOwner(services.Device.Id, local.Authority, remote.Authority)
                    ?? (elected && !peer.ChosenSyncFolders.Contains(local.Name, PlatformPaths.Comparer)))))
            .Select(local => local.Name)];
        if (common.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            var choices = LinkSyncChoices();
            var chosen = new HashSet<string>(LinkSyncChosenIn(choices, peer.Device.Id), PlatformPaths.Comparer);
            var disabled = new HashSet<string>(choices
                .Where(choice => HexLinkData.String(choice, "device", 200) == peer.Device.Id && HexLinkData.Bool(choice, "enabled") == false)
                .Select(choice => HexLinkData.String(choice, "folder", 200) ?? string.Empty), PlatformPaths.Comparer);
            int available = Math.Max(0, HexLinkHost.MOST_TREES - chosen.Count);
            foreach (string folder in common.Where(folder => !chosen.Contains(folder) && !disabled.Contains(folder)).Take(available))
            {
                if (peer.IsClosed)
                    break;
                try
                {
                    await LinkSyncStart(peer.Device.Id, folder, remember: false, automatic: true);
                }
                catch (Exception e)
                {
                    PostLink(() => Say(Said("link.sync_not_resumed", ("folder", folder), ("device", peer.Device.Name), ("why", e.Message)), error: true));
                }
            }
        });
    }

    // Link going down: every sync stopped and every remote tab's watch let go
    private void LinkShareStopAll()
    {
        LinkSyncStopAll();
        LinkFilesStopAll();
        LinkPresenceStopAll();
    }

    /// <summary>The palette's commands for sharing with each device linked now, as far as it lets this one.</summary>
    private List<(string Text, Action Picked)> LinkSharePaletteCommands()
    {
        var commands = new List<(string Text, Action Picked)>();
        foreach (HexLinkPeer peer in LinkPeers)
        {
            if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                continue;
            string device = peer.Device.Name;
            commands.Add((Said("link.command_sync", ("device", device)), () => LinkSyncCommand(peer)));
            foreach (LinkSyncShown sync in LinkSyncs.Where(each => each.DeviceId == peer.Device.Id && each.State != HexLinkSyncState.Off))
                commands.Add((Said("link.command_sync_stop", ("folder", sync.Folder), ("device", device)), () => LinkSyncStop(sync.DeviceId, sync.Folder)));
            commands.Add((Said("link.command_open_file", ("device", device)), () => LinkOpenRemoteCommand(peer)));
            commands.Add((Said("link.command_follow", ("device", device)), () => LinkFollow(peer.Device.Id)));
            commands.Add((Said("link.command_clone", ("device", device)), () => LinkCloneCommand(peer)));
            commands.Add((Said("link.command_fetch", ("device", device)), () => LinkPickRepository(repository => LinkFetchInto(peer.Device.Id, repository))));
            commands.Add((Said("link.command_offer", ("device", device)), () => LinkOfferCommand(peer)));
        }

        if (LinkConflicts.Count > 0)
            commands.Add((Said("link.command_conflicts"), LinkConflictsCommand));
        return commands;
    }

    // A folder both this workspace and the device's have, picked, synced
    private void LinkSyncCommand(HexLinkPeer peer)
    {
        IReadOnlyList<HexLinkFolder> here = linkServices?.Workspace.Folders ?? [];
        List<(string Text, Action Picked)> folders = [.. here
            .Where(folder => peer.Workspace.Folders.Any(there => PlatformPaths.Comparer.Equals(there.Name, folder.Name)))
            .Select(folder => (folder.Name, (Action)(() => LinkSyncSaying(peer.Device.Id, folder.Name))))];
        if (folders.Count == 0)
        {
            Say(Said("link.sync_no_common", ("device", peer.Device.Name)), error: true);
            return;
        }

        if (folders.Count == 1)
        {
            folders[0].Picked();
            return;
        }

        ChooseFrom(Said("link.sync_pick_title", ("device", peer.Device.Name)), Said("link.sync_pick_text"), folders);
    }

    // A sync started from a command, what came of it said
    private void LinkSyncSaying(string deviceId, string folder) => _ = Task.Run(async () =>
    {
        string? why = await LinkSyncStart(deviceId, folder);
        PostLink(() =>
        {
            if (why is not null)
                Say(why, error: true);
            else if (LinkSyncOf(deviceId, folder) is { State: not HexLinkSyncState.NeedsGit and not HexLinkSyncState.Failed } sync)
                Say(sync.Summary);
        });
    });

    // The files in conflict, one picked to look at the two of
    private void LinkConflictsCommand()
    {
        List<(string Text, Action Picked)> conflicts = [.. LinkConflicts.Select(conflict => (
            $"{conflict.Path}  ({conflict.Kind}, {conflict.DeviceName})",
            (Action)(() =>
            {
                if (!LinkOpenConflictDifference(conflict.DeviceId, conflict.Path))
                    Say(Said("link.sync_conflict_no_copy", ("path", conflict.Path)));
            })))];
        ChooseFrom(Said("link.conflicts_title"), Said("link.conflicts_text"), conflicts);
    }
}
