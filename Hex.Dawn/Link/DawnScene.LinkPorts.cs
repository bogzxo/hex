using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Numerics;
using System.Text.Json.Nodes;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Shared ports (docs/hex-link-plan.md): the Devices window's rows, the palette's commands and the hex_link_ports tool.
internal sealed partial class DawnScene
{
    private HexLinkPorts? LinkPortsOf => linkServices?.Ports;


    private void LinkPortsPaletteCommands(List<(string Text, Action Picked)> commands)
    {
        commands.Add((Said("link.ports_share"), () => LinkAskShare(null)));
        if (LinkPortsOf is { } ports && LinkPeers.Any(peer => ports.RemoteShares(peer.Device.Id).Count > 0))
        {
            List<(string Text, Action Picked)> all = [];
            foreach (HexLinkPeer peer in LinkPeers)
            {
                foreach (HexLinkRemoteShare share in ports.RemoteShares(peer.Device.Id))
                {
                    HexLinkPeer from = peer;
                    string shareId = share.Id;
                    all.Add(($"{LinkNameOf(peer.Device.Id, peer.Device.Name)}: {share.Name} ({share.Port})", () => LinkOpenPort(from, shareId)));
                }
            }

            commands.Add((Said("link.ports_pick_title"), () => ChooseFrom(Said("link.ports_pick_title"), Said("link.ports_pick_text"), all)));
        }
    }

    // Asks what to share, with one device (its id) or every device that may open ports
    private void LinkAskShare(string? deviceId)
    {
        TextBox? nameBox = null, numberBox = null, otherBox = null;
        UIDialog dialog = UIDialog.Show(chrome,
            deviceId is null ? Said("link.ports_share_title") : Said("link.ports_share_title_device", ("device", OneLine(LinkNameOf(deviceId), 30))), string.Empty,
            new DialogChoice(Said("link.ports_confirm"), () =>
            {
                string name = (nameBox?.Text ?? string.Empty).Trim(), other = (otherBox?.Text ?? string.Empty).Trim();
                if (name.Length == 0 || !int.TryParse((numberBox?.Text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                    port is < 1 or > 65535 || LinkPortsOf is not { } ports)
                {
                    Say(Said("link.ports_bad_number"));
                    return;
                }

                try
                {
                    // An address typed is a deliberate choice of somewhere else than this machine
                    HexLinkShare made = ports.Share(name, port, other.Length > 0 ? other : null, other.Length > 0, deviceId is null ? null : [deviceId]);
                    linkAudit?.Write("ports share", deviceId ?? string.Empty, $"{made.Name} {made.Host}:{made.Port}");
                }
                catch (ArgumentException e)
                {
                    Say(e.Message);
                }
            }),
            new DialogChoice(Said("link.pair_cancel")));
        dialog.Body.Size = new Vector2(Math.Clamp(chrome.Root.Bounds.Width - 80, 320, 420), 0);
        dialog.Body.Spacing = 8;
        dialog.Body.Add(LinkLine(deviceId is null ? Said("link.ports_share_about")
            : Said("link.ports_share_about_device", ("device", OneLine(LinkNameOf(deviceId), 30)))));

        UIComponent Field(string label, string placeholder, out TextBox box, StackPanel into)
        {
            var row = into.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
            row.Add(new Label { Text = UIRichText.Literal(label), TextScale = LINK_TEXT, Size = new Vector2(110, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
            box = row.Add(new TextBox { Placeholder = placeholder, Size = new Vector2(200, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left, MaxLength = 64 });
            return row;
        }

        Field(Said("link.ports_name_label"), Said("link.ports_name_placeholder"), out nameBox, dialog.Body);
        Field(Said("link.ports_number_label"), Said("link.ports_number_placeholder"), out numberBox, dialog.Body);
        var advanced = new StackPanel { Spacing = 6, Fill = UIFill.Horizontal, Visible = false };
        Field(Said("link.ports_other_address"), "127.0.0.1", out otherBox, advanced);
        advanced.Add(LinkLine(Said("link.ports_other_address_help")));
        Button toggle = LinkActionRow(dialog.Body).Add(LinkButton(Said("link.ports_advanced"), string.Empty, () => { }));
        toggle.OnPressed = () => advanced.Visible = !advanced.Visible;
        dialog.Body.Add(advanced);
        nameBox.Focus();
    }

    private void LinkOpenPort(HexLinkPeer peer, string shareId)
    {
        if (LinkPortsOf is not { } ports)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                HexLinkOpenedPort opened = await ports.OpenAsync(peer, shareId);
                PostLink(() => Say(Said("link.ports_opened", ("name", opened.Name), ("address", opened.Address))));
            }
            catch (Exception e) when (e is HexLinkException or ArgumentException or IOException)
            {
                PostLink(() => Say(Said("link.ports_failed", ("why", e.Message))));
            }
        });
    }

    private static void LinkOpenInBrowser(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://" + address) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    // The ports of one device on its card: what it shares with this machine, opened or not, and what this machine shares with it
    private void DrawLinkPortsRows(StackPanel column, HexLinkKnown known, HexLinkPeer? peer)
    {
        if (LinkPortsOf is not { } ports)
            return;
        string id = known.Device.Id;
        if (peer is not null && HexLinkAllowed.Has(peer.Granted, HexLinkRights.Ports))
        {
            IReadOnlyList<HexLinkRemoteShare> theirs = ports.RemoteShares(id);
            if (theirs.Count > 0)
                LinkRightsCaption(column, Said("link.ports_theirs", ("device", known.Shown)), string.Empty);
            foreach (HexLinkRemoteShare share in theirs)
            {
                HexLinkOpenedPort? open = ports.Open.FirstOrDefault(each => each.DeviceId == id && each.ShareId == share.Id);
                StackPanel row = LinkActionRow(column);
                row.Add(LinkLine($"{share.Name}  :{share.Port}" + (open is null ? string.Empty : "  →  " + open.Address)));
                if (open is null)
                    row.Add(LinkButton(Said("link.ports_open"), "link-external", () => LinkOpenPort(peer, share.Id)));
                else
                {
                    string address = open.Address, shareId = share.Id;
                    row.Add(LinkButton(Said("link.ports_browser"), "globe", () => LinkOpenInBrowser(address)));
                    row.Add(LinkButton(Said("link.ports_copy"), "copy", () =>
                    {
                        UIClipboard.Copy(address);
                        Say(Said("link.ports_copied", ("address", address)));
                    }));
                    row.Add(LinkButton(Said("link.ports_close"), "close", () => ports.Close(id, shareId)));
                }
            }
        }
        else if (peer is not null && ports.RemoteShares(id).Count == 0 && !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Ports))
        {
            // Nothing to show until the other device ticks the right, which the rights row above tells
        }

        // Sharing a new one is in More; what is shared already is here, to stop
        IReadOnlyList<HexLinkShare> shared = ports.Visible(id);
        if (peer is null || shared.Count == 0)
            return;
        StackPanel mine = LinkActionRow(column);
        foreach (HexLinkShare share in shared)
        {
            string shareId = share.Id;
            mine.Add(LinkButton($"{Said("link.ports_unshare")} {share.Name}:{share.Port}", "trash", () => ports.Unshare(shareId)));
        }
    }

    private HexMcpTool LinkPortsTool() => new(new McpTool("hex_link_ports", "Share and open TCP ports over Link",
        "Shared ports, like SSH -L or Live Share's shared servers. share makes a local TCP port (a dev server on 127.0.0.1) available to linked devices that you granted the Ports right, unshare stops it. On the other side open starts a listener on 127.0.0.1 (the same port if free, else the next) that tunnels every connection to the sharer's port, close removes it, and list shows what is shared, what the devices share and what is open. Only loopback targets can be shared unless allow_lan is set. At most 16 shares and 64 tunnelled connections per device; every open and connection is audited.",
        McpSchema.Object([
            ("action", McpSchema.OneOf("What to do, list by default.", "list", "share", "unshare", "open", "close")),
            ("name", McpSchema.String("share: the name to share it under. unshare, open, close: a share's name or id.")),
            ("port", McpSchema.Integer("share: the local TCP port. open: the local port to prefer.")),
            ("host", McpSchema.String("share: the target host, 127.0.0.1 unless allow_lan.")),
            ("allow_lan", McpSchema.Flag("share: explicitly allow a target that is not this machine's loopback.")),
            ("device", McpSchema.String("share: only this device (name or start of id), by default every device with the Ports right. open, close: whose share."))])),
        null, RunAway: LinkPortsToolRun, NeedsEditor: false);

    private async Task<McpToolResult> LinkPortsToolRun(JsonObject args, CancellationToken cancel)
    {
        if (!LinkOnForTools(out HexLink? links, out _) || LinkPortsOf is not { } ports)
            return McpToolResult.Text(LINK_OFF_TOOL);
        string action = args.String("action") ?? "list", name = (args.String("name") ?? string.Empty).Trim(), device = (args.String("device") ?? string.Empty).Trim();
        int port = HexLinkData.Int(args, "port") ?? 0;
        switch (action)
        {
            case "list":
            {
                var text = new StringBuilder("Shared by this device:\n");
                text.Append(ports.Shares.Count == 0 ? "  nothing\n" : string.Join('\n', ports.Shares.Select(share =>
                    $"  {share.Name}: {share.Host}:{share.Port}, {(share.Devices.Count == 0 ? "every device with the Ports right" : string.Join(", ", share.Devices.Select(id => LinkNameOf(id, id[..Math.Min(8, id.Length)]))))}")) + "\n");
                foreach (HexLinkPeer peer in links!.Peers)
                {
                    string who = LinkNameOf(peer.Device.Id, peer.Device.Name);
                    try
                    {
                        IReadOnlyList<HexLinkRemoteShare> theirs = await ports.ListAsync(peer, cancel);
                        text.Append($"Shared by {who} with this device:\n");
                        text.Append(theirs.Count == 0 ? "  nothing\n" : string.Join('\n', theirs.Select(share => $"  {share.Name}: port {share.Port}")) + "\n");
                    }
                    catch (HexLinkException e)
                    {
                        text.Append($"{who}: {e.Message}\n");
                    }
                }

                text.Append("Open here:\n").Append(ports.Open.Count == 0 ? "  nothing" : string.Join('\n', ports.Open.Select(open =>
                    $"  {open.Name} from {open.DeviceName}: {open.Address}, {open.Connections} connections")));
                return McpToolResult.Text(text.ToString());
            }
            case "share":
                if (name.Length == 0 || port == 0)
                    return McpToolResult.Error("share needs name and port.");
                try
                {
                    string[]? only = null;
                    if (device.Length > 0)
                    {
                        HexLinkKnown? known = LinkKnown.FirstOrDefault(each => each.Shown.Equals(device, StringComparison.OrdinalIgnoreCase)
                            || each.Device.Name.Equals(device, StringComparison.OrdinalIgnoreCase) || each.Device.Id.StartsWith(device, StringComparison.OrdinalIgnoreCase));
                        if (known is null)
                            return McpToolResult.Error($"No known device {device}.");
                        only = [known.Device.Id];
                    }

                    HexLinkShare made = ports.Share(name, port, args.String("host"), args.Flag("allow_lan"), only);
                    linkAudit?.Write("ports share", only?[0] ?? string.Empty, $"{made.Name} {made.Host}:{made.Port}");
                    return McpToolResult.Text($"Sharing {made.Name} ({made.Host}:{made.Port}) with {(only is null ? "every device with the Ports right" : device)}.");
                }
                catch (ArgumentException e)
                {
                    return McpToolResult.Error(e.Message);
                }
            case "unshare":
                return name.Length > 0 && ports.Unshare(name) ? McpToolResult.Text($"Stopped sharing {name}.") : McpToolResult.Error($"Nothing is shared as {name}.");
            case "open":
            case "close":
            {
                if (name.Length == 0)
                    return McpToolResult.Error($"{action} needs name, a share's name or id.");
                if (LinkPeerOf(links!.Peers, device, out string? wrong) is not { } peer)
                    return McpToolResult.Error(wrong!);
                if (action == "close")
                    return ports.Close(peer.Device.Id, name) ? McpToolResult.Text($"Closed {name}.") : McpToolResult.Error($"{name} isn't open from {peer.Device.Name}.");
                try
                {
                    HexLinkOpenedPort opened = await ports.OpenAsync(peer, name, port, cancel);
                    return McpToolResult.Text($"{opened.Name} from {peer.Device.Name} is open at {opened.Address} (their port {opened.RemotePort}).");
                }
                catch (Exception e) when (e is HexLinkException or ArgumentException)
                {
                    return McpToolResult.Error(e.Message);
                }
            }
            default:
                return McpToolResult.Error($"Unknown action {action}.");
        }
    }
}
