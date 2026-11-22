using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkClaimMark(string Holder, bool CanOpen);

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
        tab.Icon = "link";
        tab.IconColor = LinkWarning;
        tab.Tooltip += "\n" + LinkClaimLabel(claim);
    }

    private void LinkClaimExplorerRow(UITreeItem row, string path)
    {
        bool claimed = LinkClaimMarkOf(path) is not null;
        row.Note = claimed ? Said("link.devices_claimed") : string.Empty;
        row.Dot = claimed ? LinkWarning : null;
    }

    private void LinkExplorerFileMenu(ExplorerEntry entry)
    {
        Menu menu = explorer.ExplorerMenu(entry);
        AddLinkClaimMenu(menu, entry.Path);
        Context.Windows.Menu(menu);
    }

    private void AddLinkClaimMenu(Menu menu, string? path)
    {
        if (path is not null && LinkClaimMarkOf(path) is { } mark)
            menu.Add(Said("link.devices_open_shared"), () => OpenLinkClaim(path), icon: "link").IsEnabled = () => mark.CanOpen;
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
        int count = 0;
        LinkReadClaimCount(deviceId, ref count);
        if (count > 0)
            column.Add(LinkLine(Said(count == 1 ? "link.devices_one_claim" : "link.devices_claim_count",
                ("count", count.ToString(System.Globalization.CultureInfo.CurrentCulture)))));
    }

    partial void LinkReadClaimMark(string path, ref LinkClaimMark? mark);
    partial void LinkOpenClaimPath(string path, ref bool opened);
    partial void LinkReadClaimCount(string deviceId, ref int count);
}
