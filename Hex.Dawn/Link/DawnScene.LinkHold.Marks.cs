using Horizon.Rendering;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string HELD_REASON = "Held on this device";
    private const string HELD_TIP = HELD_REASON + ": not sent to others";

    private void LinkHoldExplorerMark(UITreeItem row, string path)
    {
        // The tips of an earlier mark come off first, this device's and a peer's (it is the end of the tooltip, the peer's
        // after this device's, so cutting at the first of them takes both)
        int previous = row.Tooltip.IndexOf("\n" + HELD_TIP, StringComparison.Ordinal);
        int peers = LinkHeldByTipStem() is { Length: > 0 } stem ? row.Tooltip.IndexOf("\n" + stem, StringComparison.Ordinal) : -1;
        if (peers >= 0 && (previous < 0 || peers < previous))
            previous = peers;
        if (previous >= 0)
            row.Tooltip = row.Tooltip[..previous];

        LinkRemoteHolder? holder = LinkRemoteHolderOf(path);
        HexLinkHold? held = LinkHeldFile(path);
        if (held is null && holder is null)
            return;
        row.Badge = row.Badge.Length > 0 ? row.Badge + " · H" : "H";
        row.BadgeColor = HeldColor;
        row.Tooltip = (row.Tooltip.Length > 0 ? row.Tooltip : path) + (held is null ? "" : "\n" + LinkHoldTip(held))
            + (holder is null ? "" : "\n" + LinkHeldByTip(holder));
    }

    private void LinkHoldTabMark(HexDocument shown, StripTab tab)
    {
        HexLinkHold? held = LinkHeldFile(shown.Path);
        LinkRemoteHolder? holder = LinkRemoteHolderOf(shown.Path);
        if (held is null && holder is null)
            return;
        tab.Icon = "pin";
        tab.IconColor = HeldColor;
        if (held is not null)
            tab.Tooltip += "\n" + LinkHoldTip(held);
        if (holder is not null)
            tab.Tooltip += "\n" + LinkHeldByTip(holder);
    }

    // What a peer's hold means to whoever sees it: the peer's edits to the file stay on its machine until it lets go,
    // and this device's go to the peer as an offer it takes when it likes
    private string LinkHeldByTip(LinkRemoteHolder holder) => Said("link.held_by", ("device", holder.Name));

    // The start of that tip before the device's name, to find it again in a tooltip that is made again (empty when a
    // language puts the name first, and then the tip is not cut off, it only shows twice)
    private string LinkHeldByTipStem() => Said("link.held_by", ("device", "\u0001")) is var tip && tip.IndexOf('\u0001') is > 0 and var at ? tip[..at] : string.Empty;

    private System.Numerics.Vector4 HeldColor => Skin?.Palette.Link ?? WarningColor;

    private static string LinkHoldTip(HexLinkHold held)
    {
        if (!held.HasIncoming)
            return HELD_TIP;
        string time = held.IncomingAt is { } at ? " " + LinkHoldAge(DateTimeOffset.UtcNow - at) : "";
        return $"{HELD_TIP}\nOthers changed it. Changed by {held.IncomingFrom ?? "another device"}{time}";
    }

    private static string LinkHoldAge(TimeSpan elapsed) => elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}d ago" :
        elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h ago" : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m ago" : "just now";

    private void DrawLinkFolderHolds(StackPanel holder, HexLinkPeer peer, string name)
    {
        if (linkServices?.Workspace.Folders.FirstOrDefault(folder => PlatformPaths.Comparer.Equals(folder.Name, name)) is { } folder &&
            linkServices.Holds(folder).List() is { Count: > 0 } held)
        {
            var row = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Fill = UIFill.Horizontal, Size = new(0, LINK_ROW) });
            row.Add(LinkButton($"{held.Count} held", "pin", () => ShowLinkHeld(folder)))
                .Tooltip = string.Join("\n", held.Select(entry => entry.Path));
        }

        IReadOnlyList<string> remote = LinkSyncOf(peer.Device.Id, name)?.RemoteHeldPaths ?? [];
        HexLinkSyncReport? report = peer.RemoteSyncReport.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Folder, name));
        if (remote.Count == 0 && report is { Held: > 0 })
            remote = [.. report.SkipSamples.Where(row => row.Why.StartsWith(HELD_REASON, StringComparison.Ordinal)).Select(row => row.Path)];
        int count = Math.Max(remote.Count, report?.Held ?? 0);
        if (count > 0)
        {
            var label = holder.Add(LinkLine($"{peer.Device.Name} holds {HexText.Count("file", count)}"));
            label.Tooltip = string.Join("\n", remote);
        }
    }

    private void LinkHoldMoved(LinkServices owner, HexLinkFolder folder, HexLinkHoldStore store)
    {
        if (!ReferenceEquals(linkServices, owner))
            return;

        LinkUpdateExplorerPresence();
        DecorateRows();
        tabsDirty = linkDirty = true;
        LinkMoved();
        foreach (HexLinkHold entry in store.List())
        {
            if (!entry.HasIncoming || entry.NoticeShown || !store.MarkNoticeShown(entry.Path))
                continue;
            string file = Path.GetFileName(entry.Path);
            Notify($"{entry.IncomingFrom ?? "Another device"} changed {file}, which you hold", false,
                ("Update from others", () => PreviewLinkUpdate(folder, [entry.Path])), ("Show", () => ShowLinkHeld(folder)));
        }
        LinkHoldAgedNotice(folder, store, DateTimeOffset.UtcNow);
    }

    private void CheckLinkHoldAge()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < linkHoldCheckAt || linkServices is not { } services)
            return;
        linkHoldCheckAt = now.AddMinutes(1);
        foreach (HexLinkFolder folder in services.Workspace.Folders)
            LinkHoldAgedNotice(folder, services.Holds(folder), now);
    }

    private void LinkHoldAgedNotice(HexLinkFolder folder, HexLinkHoldStore store, DateTimeOffset now)
    {
        int count = store.List().Count(entry => !entry.AgedNoticeShown && store.MarkAgedNoticeShown(entry.Path, now));
        if (count > 0)
            Notify($"{HexText.Count("file", count)} held since yesterday", false, ("Show", () => ShowLinkHeld(folder)));
    }
}
