using Horizon.Rendering;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string HELD_TIP = "Held on this device: not sent to others";

    private void LinkHoldExplorerMark(UITreeItem row, string path)
    {
        int previous = row.Tooltip.IndexOf("\n" + HELD_TIP, StringComparison.Ordinal);
        if (previous >= 0)
            row.Tooltip = row.Tooltip[..previous];
        if (LinkHeldFile(path) is not { } held)
            return;
        row.Badge = row.Badge.Length > 0 ? row.Badge + " · H" : "H";
        row.BadgeColor = HeldColor;
        row.Tooltip = (row.Tooltip.Length > 0 ? row.Tooltip : path) + "\n" + LinkHoldTip(held);
    }

    private void LinkHoldTabMark(HexDocument shown, StripTab tab)
    {
        if (LinkHeldFile(shown.Path) is not { } held)
            return;
        tab.Icon = "pin";
        tab.IconColor = HeldColor;
        tab.Tooltip += "\n" + LinkHoldTip(held);
    }

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

        if (LinkSyncOf(peer.Device.Id, name)?.RemoteHeldPaths is { Count: > 0 } remote)
        {
            var label = holder.Add(LinkLine($"{peer.Device.Name} holds {HexText.Count("file", remote.Count)}"));
            label.Tooltip = string.Join("\n", remote);
        }
    }

    private void LinkHoldMoved(LinkServices owner, HexLinkFolder folder, HexLinkHoldStore store)
    {
        if (!ReferenceEquals(linkServices, owner))
            return;

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
