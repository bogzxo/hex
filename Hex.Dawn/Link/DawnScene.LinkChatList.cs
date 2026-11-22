using System.Globalization;
using System.Net;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkChatDevice(string Id, string Name, bool Online, HexLinkRights Granted,
        IReadOnlyList<HexLinkRow> Rows, long Version, DateTime SeenUtc, string Route = "", string RouteTip = "")
    {
        public Func<int, string, string?, Task<(bool Ok, string Message)>>? Act { get; init; }
    }

    private const float LINK_CHAT_HEAD = 46.0f, LINK_CHAT_CHIP_GAP = 6.0f;
    private static readonly HexLinkChatSection[] linkChatSections = [HexLinkChatSection.Running, HexLinkChatSection.Finished];

    private static bool ShowsLinkChats(HexLinkPeer peer) =>
        !peer.IsClosed && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) && peer.Fleet.Count > 0;

    private bool LinkFleetShown
    {
        get
        {
            IReadOnlyList<HexLinkPeer> peers = LinkPeers;
            for (int i = 0; i < peers.Count; i++)
            {
                if (ShowsLinkChats(peers[i]))
                    return true;
            }
            return false;
        }
    }

    private List<LinkChatDevice> LinkChatDevices()
    {
        var devices = new List<LinkChatDevice>();
        foreach (HexLinkPeer peer in LinkPeers)
        {
            if (!ShowsLinkChats(peer))
                continue;
            string id = peer.Device.Id;
            string address = peer.Session.Connection.Address;
            string route = string.Empty, routeTip = string.Empty;
            if (address.Length > 0)
            {
                bool knownIp = IPAddress.TryParse(address, out IPAddress? ip);
                bool vpn = ip is not null && LinkIsVpn(ip);
                route = Said(!knownIp ? "link.devices_route_network" : vpn ? "link.devices_route_vpn" : "link.devices_route_lan");
                string choice = LinkKnown.FirstOrDefault(known => known.Device.Id == id)?.PreferredRoute is { } preferred
                    ? LinkRouteAddress(preferred) : Said("link.devices_route_auto");
                routeTip = Said(!knownIp ? "link.tip_route_network" : vpn ? "link.tip_route_vpn" : "link.tip_route") + " " +
                    Said("link.devices_route_tip", ("address", address), ("choice", choice));
            }
            devices.Add(new LinkChatDevice(id, LinkNameOf(id, peer.Device.Name), true, peer.Granted, peer.Fleet, peer.FleetVersion, default, route, routeTip));
        }
        devices.Sort((a, b) =>
        {
            int byName = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        return devices;
    }

    /// <summary>Adds the linked devices after the local chats and returns the height they occupy.</summary>
    private float LinkFleetSections(StackPanel rows, List<Action<float>> refreshes, float inset = 10.0f)
    {
        float tall = 0.0f;
        foreach (LinkChatDevice device in LinkChatDevices())
        {
            HexLinkChatCounts counts = HexLinkChatGroups.Count(device.Rows);
            var (head, refresh) = LinkSectionHead(device, counts, inset);
            rows.Add(head);
            refreshes.Add(refresh);
            tall += LINK_CHAT_HEAD + rows.Spacing;

            foreach (HexLinkChatSection section in linkChatSections)
            {
                int count = section == HexLinkChatSection.Running ? counts.Running : counts.Finished;
                if (count == 0)
                    continue;
                string title = Said(section == HexLinkChatSection.Running ? "link.chats_running_head" : "agents_panel.finished_head");
                rows.Add(LinkChatSectionHead(title, count, inset + 12.0f));
                tall += FINISHED_HEAD + rows.Spacing;

                foreach (var (row, step) in LinkFleetInOrder(HexLinkChatGroups.In(device.Rows, section)))
                {
                    HexAgentChat standIn = LinkStandIn(device.Id, device.Name, device.Version, row);
                    var remote = new LinkRemoteCard(device.Id, device.Name, row.Id);
                    AgentCard card = AgentCardOf(standIn, step, closable: false, quiet: section == HexLinkChatSection.Finished,
                        rail: HexLinkChatGroups.Waiting(row), remote: remote, approval: row.Approvals.FirstOrDefault());
                    rows.Add(card.Root);
                    refreshes.Add(card.Refresh);
                    tall += AGENT_CARD + rows.Spacing;
                }
            }
        }
        return tall;
    }

    private (Panel Head, Action<float> Refresh) LinkSectionHead(LinkChatDevice device, HexLinkChatCounts counts, float inset)
    {
        var head = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_CHAT_HEAD), Color = Vector4.Zero };
        string tip = Said("link.fleet_head_tip", ("device", device.Name), ("there", LinkRightsSaid(device.Granted)));
        var name = head.Add(new Label
        {
            Bold = true, TextScale = 0.13f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Position = new Vector2(inset, -3), Align = Origin.Left, Tooltip = tip
        });
        var status = LinkChatChip(Said("link.device_connected"), UIBadgeTone.Success);
        status.Dot = true;
        var upper = new List<Badge> { status };
        if (device.Route.Length > 0)
        {
            Badge route = LinkChatChip(device.Route);
            route.Tooltip = device.RouteTip;
            upper.Add(route);
        }

        string summary = HexLinkChatGroups.CountsSaid(counts, Said("link.chats_running"), Said("link.chats_finished"), string.Empty);
        var totals = head.Add(new Label
        {
            TextScale = 0.11f, Dim = true, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Position = new Vector2(inset, -26), Align = Origin.Left, Tooltip = summary
        });
        List<Badge> rights = LinkRightsChips(device.Granted);
        Badge? needsYou = counts.NeedsYou > 0
            ? LinkChatChip(Said("link.chats_needs_you", ("count", counts.NeedsYou.ToString(CultureInfo.InvariantCulture))), UIBadgeTone.Warning)
            : null;
        var lower = new List<Badge>(rights);
        if (needsYou is not null)
            lower.Add(needsYou);
        foreach (Badge chip in upper.Concat(lower))
        {
            chip.Anchor = Origin.TopRight;
            chip.Pivot = Origin.TopRight;
            chip.Colour ??= LinkBadgeColour(chip.Tone);
            head.Add(chip);
        }

        string glyph = $"[color:{HexColour(LinkDeviceTint(device.Id, Skin))}]$(vm)[/color] ";
        int fitted = -1;
        void Refresh(float width)
        {
            if (fitted == (int)width)
                return;
            fitted = (int)width;
            float top = PlaceLinkChatChips(upper, -3);
            name.Text = glyph + UIRichText.Literal(FitText(device.Name, name.TextScale, width - inset - top - 24, bold: true));
            float minSummary = (Skin?.Font.Measure(summary, totals.TextScale, markup: false).X ?? 115.0f) + inset + 6;
            float lowerWidth = lower.Sum(chip => LinkChatChipWidth(chip) + LINK_CHAT_CHIP_GAP) + 8;
            foreach (Badge chip in rights)
                chip.Visible = minSummary + lowerWidth <= width;
            float bottom = PlaceLinkChatChips(lower, -26);
            totals.Text = UIRichText.Literal(FitText(summary, totals.TextScale, width - inset - bottom));
        }
        return (head, Refresh);
    }

    private Badge LinkChatChip(string text, UIBadgeTone tone = UIBadgeTone.Neutral) =>
        new() { Text = text, Tone = tone, Colour = LinkBadgeColour(tone), TextScale = 0.11f };

    private float LinkChatChipWidth(Badge chip)
    {
        if (Skin is not { } skin)
            return 80.0f;
        float line = skin.Font.LineHeight * chip.TextScale;
        float text = skin.Font.Measure(chip.Text, chip.TextScale, markup: false).X;
        return MathF.Ceiling(text + 16 + (chip.Icon.Length > 0 ? line + 4 : 0) + (chip.Dot ? line * 0.45f + 4 : 0));
    }

    private float PlaceLinkChatChips(List<Badge> chips, float y)
    {
        float right = 8.0f;
        for (int i = chips.Count - 1; i >= 0; i--)
        {
            Badge chip = chips[i];
            if (!chip.Visible)
                continue;
            chip.Position = new Vector2(-right, y);
            right += LinkChatChipWidth(chip) + LINK_CHAT_CHIP_GAP;
        }
        return right;
    }

    private UIComponent LinkChatSectionHead(string title, int count, float inset)
    {
        var row = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, FINISHED_HEAD), Color = Vector4.Zero };
        row.Add(new Label
        {
            Text = title, TextScale = 0.125f, Dim = true, Bold = true,
            Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(inset, 0), Align = Origin.Left
        });
        Badge chip = LinkChatChip(count.ToString(CultureInfo.InvariantCulture));
        chip.Anchor = Origin.Right;
        chip.Pivot = Origin.Right;
        chip.Position = new Vector2(-8, 0);
        row.Add(chip);
        return row;
    }
}
