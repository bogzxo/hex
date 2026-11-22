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

    private static string LinkClaimLabel(LinkClaimMark mark) => Said("link.devices_claimed_by", ("holder", mark.Holder));

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
        if (LinkClaimMarkOf(path) is { } claim)
        {
            row.Icon = "lock";
            row.IconColor = LinkDeviceTint(claim.DeviceId, Skin);
            row.Tooltip = path + "\n" + LinkClaimLabel(claim);
        }
        else if (row.Icon == "lock" && row.Tooltip.Length > path.Length)
        {
            row.Icon = HexExplorer.FileIcon(path);
            row.IconColor = explorer.IsCutPath(path) ? DimColor : explorer.FileIconColor(path);
            row.Tooltip = path;
        }
    }

    private void LinkExplorerFileMenu(ExplorerEntry entry)
    {
        Menu menu = explorer.ExplorerMenu(entry);
        AddLinkClaimMenu(menu, entry.Path);
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
