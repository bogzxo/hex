using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What Link's window shows of the code shared with linked devices (docs/hex-link-plan.md, 5.3 to 5.5), over what the
// sharing part keeps (DawnScene.LinkSync, DawnScene.LinkGit). Under each connected device a row for each workspace
// folder both machines have open: how its sync is doing in a word and a line, how many files went each way, whether
// it only pulls (the other doesn't let this one write there), and Start or Stop, with Sync Anyway when the two have
// different commits checked out. Then every file in conflict, with Keep Mine, Take Theirs and Open Difference, and the
// changes other devices offered, with Apply, Keep as File and Discard. Nothing of it is there while nothing's shared.
internal sealed partial class DawnScene
{
    // The part of the window the conflicts and offered changes are in, made again with the rest
    private StackPanel? linkWindowShare;

    /// <summary>The folders a linked device and this machine both have open, by name, what a sync can be of.</summary>
    private List<string> LinkSharedFolders(HexLinkPeer peer)
    {
        var here = new HashSet<string>(explorer.workspace.Folders.Select(folder => Path.GetFileName(folder.TrimEnd('/', '\\'))), StringComparer.OrdinalIgnoreCase);
        return [.. peer.Workspace.Folders.Select(folder => folder.Name).Where(here.Contains).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A connected device's sync rows, a folder each, put under its card's rights.</summary>
    private void LinkDeviceSyncRows(StackPanel column, HexLinkPeer peer)
    {
        List<string> folders = LinkSharedFolders(peer);
        IReadOnlyList<LinkSyncShown> syncs = LinkSyncs;
        // A sync kept for a folder that isn't open on both now still shows, to be stopped
        foreach (LinkSyncShown kept in syncs)
        {
            if (kept.DeviceId.Equals(peer.Device.Id, StringComparison.OrdinalIgnoreCase) && !folders.Contains(kept.Folder, StringComparer.OrdinalIgnoreCase))
                folders.Add(kept.Folder);
        }

        if (folders.Count == 0)
        {
            column.Add(LinkLine(Said("link.devices_sync_none")));
            return;
        }
        column.Add(new Label { Text = Said("link.sync_heading"), TextScale = LINK_SMALL, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        foreach (string folder in folders)
        {
            LinkSyncShown? sync = syncs.FirstOrDefault(each => each.DeviceId.Equals(peer.Device.Id, StringComparison.OrdinalIgnoreCase) && each.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
            column.Add(LinkSyncRow(peer, folder, sync));
        }
    }

    // A folder's sync with a device: its name and state, what it says, the counts, and its buttons
    private UIComponent LinkSyncRow(HexLinkPeer peer, string folder, LinkSyncShown? sync)
    {
        string id = peer.Device.Id;
        var holder = new StackPanel { Spacing = 2.0f, Fill = UIFill.Horizontal, Padding = new UIEdges(12, 0, 0, 0) };
        var row = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        row.Add(new Label { Text = $"$(folder) [b]{UIRichText.Literal(OneLine(folder, 25))}[/b]", TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = folder });
        HexLinkSyncState state = sync?.State ?? HexLinkSyncState.Off;
        row.Add(new Badge
        {
            Text = Said("link.sync_state_" + state.ToString().ToLowerInvariant()),
            Tone = state switch
            {
                HexLinkSyncState.Idle => UIBadgeTone.Success,
                HexLinkSyncState.Syncing or HexLinkSyncState.Comparing => UIBadgeTone.Info,
                HexLinkSyncState.NeedsGit or HexLinkSyncState.PullOnly => UIBadgeTone.Warning,
                HexLinkSyncState.Failed => UIBadgeTone.Danger,
                _ => UIBadgeTone.Neutral
            },
            Colour = state is HexLinkSyncState.Syncing or HexLinkSyncState.Comparing ? LinkBadgeInfoColour : null,
            Dot = state is HexLinkSyncState.Syncing or HexLinkSyncState.Comparing, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left
        });
        if (sync is { PullOnly: true } && state != HexLinkSyncState.PullOnly)
            row.Add(new Badge { Text = Said("link.sync_state_pullonly"), Tone = UIBadgeTone.Warning, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("link.sync_pull_only_tip") });
        if (sync is not null && (sync.Sent != 0 || sync.Received != 0 || sync.Conflicts != 0 || sync.Skipped != 0))
        {
            Label counts = holder.Add(LinkLine(Said("link.sync_counts", ("sent", sync.Sent.ToString(CultureInfo.InvariantCulture)),
                ("received", sync.Received.ToString(CultureInfo.InvariantCulture)), ("conflicts", sync.Conflicts.ToString(CultureInfo.InvariantCulture)),
                ("skipped", sync.Skipped.ToString(CultureInfo.InvariantCulture)))));
            if (sync.Skipped > 0)
                counts.Tooltip = string.Join("\n", LinkSyncSkipped(id, folder).Take(20).Select(each => each.ToString()));
        }

        bool running = state is HexLinkSyncState.Comparing or HexLinkSyncState.Syncing or HexLinkSyncState.Idle or HexLinkSyncState.PullOnly or HexLinkSyncState.NeedsGit;
        if (running)
            row.Add(LinkButton(Said("link.sync_stop"), "debug-stop", () => LinkSyncStop(id, folder)));
        else
            row.Add(LinkButton(Said(state == HexLinkSyncState.Failed ? "link.devices_try_now" : "link.sync_start"), "sync", () => LinkSyncGo(id, folder, force: false))).Enabled = HexLinkAllowed.Has(peer.Granted, HexLinkRights.View);
        if (state == HexLinkSyncState.NeedsGit)
            row.Add(LinkButton(Said("link.sync_anyway"), "warning", () => LinkSyncGo(id, folder, force: true)));

        if (sync is not null)
            DrawLinkSyncProgress(holder, sync);

        string said = sync?.Why ?? string.Empty;
        if (said.Length > 0)
            holder.Add(new Label { Text = UIRichText.Literal(said), TextScale = LINK_SMALL, Dim = true, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal, Padding = new UIEdges(18, 0, 0, 0) });
        return holder;
    }

    private void DrawLinkSyncProgress(StackPanel holder, LinkSyncShown sync)
    {
        int filesDone = 0, filesTotal = 0;
        long bytesDone = 0, bytesTotal = 0;
        int queued = 0;
        string? detail = null;
        LinkDeviceSyncProgress(sync, ref filesDone, ref filesTotal, ref bytesDone, ref bytesTotal, ref queued, ref detail);
        if (filesTotal > 0 || bytesTotal > 0)
        {
            holder.Add(new ProgressBar
            {
                Size = new Vector2(0, 9), Fill = UIFill.Horizontal, ShowText = false,
                Progress = bytesTotal > 0 ? (float)((double)bytesDone / bytesTotal) : (float)filesDone / filesTotal
            });
            var counts = new List<string>(3);
            if (filesTotal > 0)
                counts.Add(Said("link.devices_sync_progress", ("done", filesDone.ToString(CultureInfo.InvariantCulture)), ("total", filesTotal.ToString(CultureInfo.InvariantCulture))));
            if (bytesTotal > 0)
                counts.Add(Said("link.devices_sync_bytes", ("done", (bytesDone / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)),
                    ("total", (bytesTotal / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture))));
            if (queued > 0)
                counts.Add(Said("link.devices_sync_queue", ("count", queued.ToString(CultureInfo.InvariantCulture))));
            holder.Add(LinkLine(string.Join(" · ", counts)));
        }

        if (detail is { Length: > 0 })
            holder.Add(LinkLine(detail));
    }

    // Worker A supplies counters for the active pass when the sync engine reports them.
    partial void LinkDeviceSyncProgress(LinkSyncShown sync, ref int filesDone, ref int filesTotal, ref long bytesDone, ref long bytesTotal, ref int queued, ref string? detail);

    // A sync started from the window, why it couldn't said
    private void LinkSyncGo(string deviceId, string folder, bool force)
    {
        _ = Task.Run(async () =>
        {
            string? why;
            try
            {
                why = await LinkSyncStart(deviceId, folder, force);
            }
            catch (Exception e)
            {
                why = e.Message;
            }

            if (why is not null)
                PostLink(() => Say(Said("link.sync_failed", ("folder", folder), ("why", why)), error: true));
        });
    }

    /// <summary>The conflicts and the changes offered, made again with the rest of the window.</summary>
    private void DrawLinkShare()
    {
        if (linkWindowShare is not { } share)
            return;
        ClearRows(share);
        IReadOnlyList<LinkConflictShown> conflicts = LinkConflicts;
        IReadOnlyList<LinkPatchOffer> offered = LinkPatchesOffered;
        if (conflicts.Count > 0)
        {
            share.Add(LinkHeading(Said("link.conflicts_heading", ("count", conflicts.Count.ToString(CultureInfo.InvariantCulture)))));
            foreach (LinkConflictShown conflict in conflicts)
                share.Add(LinkConflictRow(conflict));
        }

        // The other devices' files open here, each opened again with a press
        IReadOnlyList<LinkRemoteShown> remote = LinkRemoteFiles;
        if (remote.Count > 0)
        {
            share.Add(LinkHeading(Said("link.remote_files_heading", ("count", remote.Count.ToString(CultureInfo.InvariantCulture)))));
            foreach (LinkRemoteShown file in remote)
            {
                var row = share.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
                string said = Said(file.CanEdit ? "link.remote_file" : "link.remote_file_read_only", ("device", LinkNameOf(file.DeviceId, file.DeviceName)));
                row.Add(new Label
                {
                    Text = $"[color:{HexColour(LinkDeviceTint(file.DeviceId, Skin))}]$(vm)[/color] [b]{UIRichText.Literal(file.Wire)}[/b]  {UIRichText.Literal(said)}",
                    TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = file.Copy
                });
                if (file.MovedThere)
                    row.Add(new Badge { Text = Said("link.remote_file_moved"), Tone = UIBadgeTone.Warning, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
                string device = file.DeviceId, wire = file.Wire;
                row.Add(LinkButton(Said("link.remote_file_open"), "go-to-file", () => _ = LinkOpenRemote(device, wire)));
            }
        }

        if (offered.Count > 0)
        {
            share.Add(LinkHeading(Said("link.patches_heading", ("count", offered.Count.ToString(CultureInfo.InvariantCulture)))));
            foreach (LinkPatchOffer offer in offered)
                share.Add(LinkPatchRow(offer));
        }
    }

    // A file in conflict: where, what happened, and the three ways out
    private UIComponent LinkConflictRow(LinkConflictShown conflict)
    {
        Panel card = StripedCard(LinkWarning);
        var column = card.Add(new StackPanel { Spacing = 4.0f, Fill = UIFill.Horizontal });
        column.Add(new Label
        {
            Text = $"[b]{UIRichText.Literal(conflict.Path)}[/b]  {UIRichText.Literal(Said("link.conflict_with", ("device", LinkNameOf(conflict.DeviceId, conflict.DeviceName)), ("kind", conflict.Kind)))}",
            TextScale = LINK_TEXT, Align = Origin.Left, Fill = UIFill.Horizontal, Tooltip = conflict.Copy ?? string.Empty
        });
        var buttons = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        buttons.Add(LinkButton(Said("link.conflict_mine"), "arrow-up", () => LinkResolveGo(conflict, HexLinkSyncKeep.Mine)));
        buttons.Add(LinkButton(Said("link.conflict_theirs"), "arrow-down", () => LinkResolveGo(conflict, HexLinkSyncKeep.Theirs)));
        buttons.Add(LinkButton(Said("link.conflict_difference"), "diff", () => LinkOpenConflictDifference(conflict.DeviceId, conflict.Path)));
        return card;
    }

    private void LinkResolveGo(LinkConflictShown conflict, HexLinkSyncKeep keep)
    {
        _ = Task.Run(async () =>
        {
            string? why;
            try
            {
                why = await LinkResolve(conflict.DeviceId, conflict.Path, keep);
            }
            catch (Exception e)
            {
                why = e.Message;
            }

            if (why is not null)
                PostLink(() => Say(Said("link.conflict_failed", ("path", conflict.Path), ("why", why)), error: true));
        });
    }

    // Changes another device offered: who, the title, how big, and what to do with them
    private UIComponent LinkPatchRow(LinkPatchOffer offer)
    {
        Panel card = StripedCard(LinkDeviceTint(offer.Device.Id, Skin));
        var column = card.Add(new StackPanel { Spacing = 4.0f, Fill = UIFill.Horizontal });
        int lines = offer.Diff.Count(c => c == '\n');
        column.Add(new Label
        {
            Text = $"[b]{UIRichText.Literal(OneLine(offer.Title, 120))}[/b]  {UIRichText.Literal(Said("link.patch_from", ("device", LinkNameOf(offer.Device.Id, offer.Device.Name)), ("lines", lines.ToString(CultureInfo.InvariantCulture))))}",
            TextScale = LINK_TEXT, Align = Origin.Left, Fill = UIFill.Horizontal
        });
        var buttons = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        buttons.Add(LinkButton(Said("link.patch_apply"), "check", () => LinkPatchSaid(offer, "applied"))).Primary = true;
        buttons.Add(LinkButton(Said("link.patch_keep"), "save", () => LinkPatchSaid(offer, "kept")));
        buttons.Add(LinkButton(Said("link.patch_discard"), "trash", () => LinkPatchSaid(offer, "discarded")));
        return card;
    }

    // What the person did with offered changes, said as the sharing part says it
    private void LinkPatchSaid(LinkPatchOffer offer, string decision)
    {
        string said = LinkDecidePatch(offer, decision);
        if (said.Length > 0)
            Say(said);
        linkWindowStale = true;
    }
}
