using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkClaimMark(string Holder, bool CanOpen, string DeviceId);

    private LinkClaimMark? LinkClaimMarkOf(string? path)
    {
        LinkClaimMark? mark = null;
        if (path is not null)
            LinkReadClaimMark(path, ref mark);
        return mark;
    }

    // "Alpha has this file open" for somebody only looking at it, "also editing" only for a tab changed here too
    private string LinkClaimLabel(LinkClaimMark mark, bool editingHere = false) =>
        Said(editingHere ? "link.devices_claimed_by" : "link.devices_claimed_open", ("device", LinkNameOf(mark.DeviceId, mark.Holder)));

    private void LinkClaimTab(HexDocument shown, StripTab tab)
    {
        if (LinkClaimMarkOf(shown.Path) is not { } claim)
            return;
        tab.Icon = "lock";
        tab.IconColor = LinkDeviceTint(claim.DeviceId, Skin);
        tab.Tooltip += "\n" + LinkClaimLabel(claim);
    }

    private void LinkClaimExplorerRow(UITreeItem row, string path)
    {
        // A lock says you can't edit it; when both devices are editing together nobody is locked out, so no lock then
        IReadOnlyList<HexLinkFileMark> marks = LinkFileMarksOf(path);
        bool together = marks.Any(mark => mark.Own && mark.Kind == HexLinkMarkKind.Editing)
            && marks.Any(mark => !mark.Own && mark.Kind == HexLinkMarkKind.Editing);
        if (!together && LinkClaimMarkOf(path) is { } claim)
        {
            row.Icon = "lock";
            row.IconColor = LinkDeviceTint(claim.DeviceId, Skin);
            // The marks' lines below say who holds it; the claim's own line only when they have nothing
            row.Tooltip = LinkFileMarksOf(path).Count > 0 ? path : path + "\n" + LinkClaimLabel(claim);
        }
        else if (row.Icon == "lock")
        {
            row.Icon = HexExplorer.FileIcon(path);
            row.IconColor = explorer.IsCutPath(path) ? DimColor : explorer.FileIconColor(path);
            row.Tooltip = path;
        }

        LinkMarkTooltip(row, path);
    }

    private void LinkExplorerFileMenu(ExplorerEntry entry, bool includeClaims = true)
    {
        Menu menu = explorer.ExplorerMenu(entry);
        if (!entry.IsFolder)
            AddLinkMenu(menu, entry.Path, includeClaims);
        Context.Windows.Menu(menu);
    }

    private void AddLinkClaimMenu(Menu menu, string? path)
    {
        if (path is not null && LinkClaimMarkOf(path) is { CanOpen: true })
            menu.Add(Said("link.devices_open_shared"), () => OpenLinkClaim(path), icon: "link");
    }

    private void OpenLinkClaim(string path)
    {
        bool opened = false;
        LinkOpenClaimPath(path, ref opened);
        if (!opened)
            Say(Said("link.devices_claim_cannot_open"), error: true);
    }

    private void LinkClaimSurfacesChanged()
    {
        tabsDirty = true;
        MarkLinkEditorNotices();
        LinkUpdateExplorerPresence();
        if (Side == HexSide.Explorer)
            DecorateRows();
    }

    private void DrawLinkClaimCount(StackPanel column, string deviceId)
    {
        IReadOnlyList<string> paths = [];
        LinkReadClaimPaths(deviceId, ref paths);
        if (paths.Count == 0)
            return;

        string first = OneLine(Path.GetFileName(paths[0].TrimEnd('/')), 65);
        var label = LinkLine(Said(paths.Count == 1 ? "link.devices_one_claim" : "link.devices_claim_count",
            ("count", paths.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)), ("file", first)));
        label.Tooltip = string.Join("\n", paths);
        column.Add(label);
    }

    partial void LinkReadClaimMark(string path, ref LinkClaimMark? mark);
    partial void LinkOpenClaimPath(string path, ref bool opened);
    partial void LinkReadClaimPaths(string deviceId, ref IReadOnlyList<string> paths);
}
