using System.Globalization;
using System.Net;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

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

    private HexChatListFolds? linkChatFolds;
    private Dictionary<string, bool> linkChatUsualSeen { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.Ordinal)); }
    private int linkChatFoldsVersion;
    private (UIComponent List, ChatListHeadButton Previous)? linkChatRefocus;

    private HexChatListFolds LinkChatFolds => linkChatFolds ??= new HexChatListFolds(settings.AgentsListFolds);

    private bool LinkChatOpen(string key, bool usual)
    {
        linkChatUsualSeen.TryAdd(key, usual);
        return LinkChatFolds.Open(key, linkChatUsualSeen[key]);
    }

    private void SetLinkChatOpen(string key, bool open)
    {
        LinkChatFolds.Set(key, open);
        SaveLinkChatFolds();
    }

    private void SaveLinkChatFolds()
    {
        settings.AgentsListFolds = LinkChatFolds.ToString();
        SaveSettings();
        linkChatFoldsVersion++;
        agentsListDrawn = noonFleetDrawn = NOT_DRAWN;
        noonWait = 0.0f;
        Engine.WindowManager.RequestFrames();
    }

    private Dictionary<string, (LinkChatDevice Device, string Address, HexLinkEndpoint? Preferred)> linkChatsLast { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private Dictionary<string, DateTime> linkChatsSeenAt { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private Dictionary<(string Device, int Id), string> linkRowStates { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private HashSet<(string Device, int Id)> linkFailedUnseen { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }
    private List<LinkChatDevice>? linkChatFakes = null;

    private static bool ShowsLinkChats(HexLinkPeer peer) =>
        !peer.IsClosed && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) && peer.Fleet.Count > 0;

    private static HexLinkPeer? FindLinkChatPeer(IReadOnlyList<HexLinkPeer> peers, string id)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            HexLinkPeer peer = peers[i];
            if (!peer.IsClosed && peer.Device.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return peer;
        }
        return null;
    }

    private bool LinkFleetShown
    {
        get
        {
            if (linkChatFakes is { Count: > 0 })
                return true;
            IReadOnlyList<HexLinkPeer> peers = LinkPeers;
            for (int i = 0; i < peers.Count; i++)
            {
                if (ShowsLinkChats(peers[i]))
                    return true;
            }
            foreach (string id in linkChatsLast.Keys)
            {
                if (linkTrust?.Find(id) is not null && FindLinkChatPeer(peers, id) is null)
                    return true;
            }
            return false;
        }
    }

    // Keep the last fleet even when neither chat list is visible.
    private void CaptureLinkChatDevices(IReadOnlyList<HexLinkPeer> peers)
    {
        foreach (string id in linkChatsLast.Keys.ToArray())
        {
            HexLinkPeer? peer = FindLinkChatPeer(peers, id);
            if (linkTrust?.Find(id) is not null && (peer is null || HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)
                && (peer.Fleet.Count > 0 || peer.FleetVersion == 0)))
                continue;
            linkChatsLast.Remove(id);
            linkChatsSeenAt.Remove(id);
            RemoveLinkStandIns(id);
            foreach (var key in linkRowStates.Keys.Where(key => key.Device == id).ToArray())
            {
                linkRowStates.Remove(key);
                linkFailedUnseen.Remove(key);
            }
        }

        for (int i = 0; i < peers.Count; i++)
        {
            HexLinkPeer peer = peers[i];
            if (!ShowsLinkChats(peer))
                continue;
            string id = peer.Device.Id;
            IReadOnlyList<HexLinkRow> rows = peer.Fleet;
            long version = peer.FleetVersion;
            string name = LinkNameOf(id, peer.Device.Name);
            string address = peer.Session.Connection.Address;
            HexLinkEndpoint? preferred = linkTrust?.Find(id)?.PreferredRoute;
            linkChatsSeenAt[id] = DateTime.UtcNow;
            if (linkChatsLast.TryGetValue(id, out var last) && ReferenceEquals(last.Device.Rows, rows)
                && last.Device.Version == version && last.Device.Name == name && last.Device.Granted == peer.Granted
                && last.Address == address && last.Preferred == preferred)
                continue;

            string route = string.Empty, routeTip = string.Empty;
            if (address.Length > 0)
            {
                bool knownIp = IPAddress.TryParse(address, out IPAddress? ip);
                bool vpn = ip is not null && LinkIsVpn(ip);
                route = Said(!knownIp ? "link.devices_route_network" : vpn ? "link.devices_route_vpn" : "link.devices_route_lan");
                string choice = preferred is { } chosen ? LinkRouteAddress(chosen) : Said("link.devices_route_auto");
                routeTip = Said(!knownIp ? "link.tip_route_network" : vpn ? "link.tip_route_vpn" : "link.tip_route") + " " +
                    Said("link.devices_route_tip", ("address", address), ("choice", choice));
            }
            var device = new LinkChatDevice(id, name, true, peer.Granted, rows, version, default, route, routeTip);
            linkChatsLast[id] = (device, address, preferred);
            TrackLinkFailures(device);
        }
    }

    private List<LinkChatDevice> LinkChatDevices()
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        CaptureLinkChatDevices(peers);
        var devices = new List<LinkChatDevice>();
        foreach (var (id, last) in linkChatsLast)
        {
            if (FindLinkChatPeer(peers, id) is { } peer)
            {
                if (ShowsLinkChats(peer))
                    devices.Add(last.Device);
            }
            else if (linkTrust?.Find(id) is { } known)
                devices.Add(last.Device with { Name = known.Shown, Online = false, SeenUtc = linkChatsSeenAt.GetValueOrDefault(id, known.Seen) });
        }
        if (linkChatFakes is not null)
        {
            foreach (LinkChatDevice fake in linkChatFakes)
            {
                TrackLinkFailures(fake);
                devices.Add(fake);
            }
        }
        devices.Sort((a, b) =>
        {
            int byName = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        return devices;
    }

    private LinkChatDevice? LinkChatFake(string id)
    {
        if (linkChatFakes is not { } fakes)
            return null;
        for (int i = 0; i < fakes.Count; i++)
        {
            if (fakes[i].Id == id)
                return fakes[i];
        }
        return null;
    }

    private bool LinkChatOnline(string id) => LinkChatFake(id)?.Online ?? (LinkPeerOf(id) is not null);

    private string LinkChatOfflineReason(string name) => Said("link.chats_offline_reason", ("device", name));

    private void TrackLinkFailures(LinkChatDevice device)
    {
        foreach (HexLinkRow row in device.Rows)
        {
            var key = (device.Id, row.Id);
            bool wasKnown = linkRowStates.TryGetValue(key, out string? before);
            if (row.State != "failed")
                linkFailedUnseen.Remove(key);
            else if (wasKnown && before != "failed" && (linkRemoteOpen?.DeviceId != device.Id || linkRemoteOpen.Id != row.Id))
                linkFailedUnseen.Add(key);
            linkRowStates[key] = row.State;
        }
    }

    private readonly record struct LinkChatGroupState(LinkChatDevice Device, HexLinkChatCounts Counts, bool Open,
        bool RunningOpen, bool FinishedOpen, int SelectedChat, UISkin? Skin);
    private sealed record LinkChatGroupView(LinkChatGroupState State, StackPanel Root, float Height, Action<float> Refresh);
    private Dictionary<string, LinkChatGroupView> agentsLinkChatGroups { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.Ordinal)); }
    private Dictionary<string, LinkChatGroupView> noonLinkChatGroups { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.Ordinal)); }

    /// <summary>Adds the linked devices after the local chats and returns the height they occupy.</summary>
    private float LinkFleetSections(StackPanel rows, List<Action<float>> refreshes, float inset = 10.0f)
    {
        float tall = 0.0f;
        bool side = rows == agentsListRows;
        ScrollPanel scroll = side ? agentsListScroll : noonFleetScroll;
        float trim = scroll.Padding.Total.X + scroll.BarWidth + scroll.BarGap + (side ? agentsList.Padding.Total.X : -6.0f);
        Dictionary<string, LinkChatGroupView> groups = side ? agentsLinkChatGroups : noonLinkChatGroups;
        List<LinkChatDevice> devices = LinkChatDevices();
        if (groups.Count > 0)
        {
            foreach (string id in groups.Keys.Where(id => !devices.Any(device => device.Id == id)).ToArray())
                groups.Remove(id);
        }
        foreach (LinkChatDevice device in devices)
        {
            HexLinkChatCounts counts = HexLinkChatGroups.Count(device.Rows, row => LinkFailureUnseen(device, row));
            bool open = LinkChatOpen(device.Id, HexLinkChatGroups.DeviceUsuallyOpen(counts));
            bool running = counts.Running > 0 && LinkChatOpen(HexLinkChatGroups.SectionKey(device.Id, HexLinkChatSection.Running), true);
            bool finished = counts.Finished > 0 && LinkChatOpen(HexLinkChatGroups.SectionKey(device.Id, HexLinkChatSection.Finished),
                HexLinkChatGroups.SectionUsuallyOpen(HexLinkChatSection.Finished, counts.Finished));
            int selected = linkRemoteOpen is { } picked && picked.DeviceId == device.Id ? picked.Id : 0;
            var state = new LinkChatGroupState(device, counts, open, running, finished, selected, Skin);
            if (!groups.TryGetValue(device.Id, out LinkChatGroupView? group) || group.State != state)
                groups[device.Id] = group = MakeLinkChatGroup(state, rows.Spacing, inset, trim);
            rows.Add(group.Root);
            refreshes.Add(group.Refresh);
            tall += group.Height;
        }
        RefocusChatListHead(rows);
        return tall;
    }

    private bool LinkFailureUnseen(LinkChatDevice device, HexLinkRow row) => linkFailedUnseen.Contains((device.Id, row.Id));

    private LinkChatGroupView MakeLinkChatGroup(LinkChatGroupState state, float spacing, float inset, float trim)
    {
        LinkChatDevice device = state.Device;
        var block = new StackPanel { Fill = UIFill.Horizontal, Spacing = spacing };
        var (head, fitHead) = LinkSectionHead(device, state.Counts, inset, device.Id, state.Open);
        var fitRows = new List<Action<float>>();
        block.Add(head);
        float tall = LINK_CHAT_HEAD + spacing;
        if (state.Open)
        {
            PrepareLinkStandIns(device);
            foreach (HexLinkChatSection section in linkChatSections)
            {
                bool running = section == HexLinkChatSection.Running;
                int count = running ? state.Counts.Running : state.Counts.Finished;
                if (count == 0)
                    continue;
                string sectionKey = HexLinkChatGroups.SectionKey(device.Id, section);
                bool sectionOpen = running ? state.RunningOpen : state.FinishedOpen;
                string title = Said(running ? "link.chats_running_head" : "agents_panel.finished_head");
                block.Add(ListSectionHead(sectionKey, title, count, string.Empty, UIBadgeTone.Neutral, sectionOpen,
                    shown => SetLinkChatOpen(sectionKey, shown), menu: null, inset + 12.0f));
                tall += FINISHED_HEAD + spacing;
                if (!sectionOpen)
                    continue;

                foreach (var (row, step) in LinkFleetInOrder(HexLinkChatGroups.In(device.Rows, section)))
                {
                    HexAgentChat standIn = LinkStandIn(device.Id, device.Name, row);
                    var remote = new LinkRemoteCard(device.Id, device.Name, row.Id);
                    AgentCard card = AgentCardOf(standIn, step, closable: false, quiet: !device.Online || !running,
                        rail: HexLinkChatGroups.NeedsYou(row, item => LinkFailureUnseen(device, item)), remote: remote,
                        approval: row.Approvals.FirstOrDefault(), waiting: row.Waiting);
                    block.Add(card.Root);
                    fitRows.Add(card.Refresh);
                    tall += AGENT_CARD + spacing;
                }
            }
        }

        float fittedWidth = float.NaN;
        void Refresh(float width)
        {
            if (width == fittedWidth)
                return;
            fittedWidth = width;
            fitHead(width - trim);
            foreach (Action<float> fit in fitRows)
                fit(width - trim);
        }
        return new LinkChatGroupView(state, block, tall, Refresh);
    }

    private (Panel Head, Action<float> Refresh) LinkSectionHead(LinkChatDevice device, HexLinkChatCounts counts, float inset, string key, bool open)
    {
        var head = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_CHAT_HEAD), Color = Vector4.Zero };
        string tip = Said("link.fleet_head_tip", ("device", device.Name), ("there", LinkRightsSaid(device.Granted)));
        if (!device.Online)
            tip += "\n" + LinkChatOfflineReason(device.Name);
        head.Add(new ChatListHeadButton(this, key, open, shown => SetLinkChatOpen(key, shown))
        {
            Tooltip = tip,
            OnContextMenu = at => ContextMenu.Show(chrome, at, LinkDeviceMenu(device))
        });
        var name = head.Add(new Label
        {
            Bold = true, Dim = !device.Online, Color = device.Online ? null : LinkChatDimInk(),
            TextScale = 0.13f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
            Position = new Vector2(inset, -3), Align = Origin.Left, Tooltip = tip
        });
        var status = device.Online ? LinkChatChip(Said("link.device_connected"), UIBadgeTone.Success)
            : LinkChatChip(Said("link.chats_offline"));
        status.Dot = device.Online;
        if (!device.Online)
            status.Tooltip = LinkChatOfflineReason(device.Name);
        var upper = new List<Badge> { status };
        if (device.Online && device.Route.Length > 0)
        {
            Badge route = LinkChatChip(device.Route);
            route.Tooltip = device.RouteTip;
            upper.Add(route);
        }

        string countsSaid = HexLinkChatGroups.CountsSaid(counts, Said("link.chats_running"), Said("link.chats_finished"), string.Empty);
        string summary = countsSaid, compactSummary = countsSaid;
        if (!device.Online)
        {
            DateTime seen = device.SeenUtc.ToLocalTime();
            string lastSeen = Said("link.chats_offline_seen", ("time", seen.ToString("g", CultureInfo.CurrentCulture)));
            summary = JoinAgentCardDetails(lastSeen, countsSaid);
            compactSummary = JoinAgentCardDetails(seen.ToString("M/d HH:mm", CultureInfo.CurrentCulture), countsSaid);
            status.Tooltip = $"{lastSeen}\n{status.Tooltip}";
        }
        var totals = head.Add(new Label
        {
            TextScale = 0.11f, Dim = true, Color = LinkChatDimInk(), Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
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

        string glyph = $"$({(open ? "chevron-down" : "chevron-right")}) [color:{HexColour(LinkDeviceTint(device.Id, Skin))}]$(vm)[/color] ";
        int fitted = -1;
        void Refresh(float width)
        {
            if (fitted == (int)width)
                return;
            fitted = (int)width;
            float top = PlaceLinkChatChips(upper, -3);
            name.Text = glyph + UIRichText.Literal(FitText(device.Name, name.TextScale, width - inset - top - 38, bold: true));
            float minSummary = (Skin?.Font.Measure(summary, totals.TextScale, markup: false).X ?? 115.0f) + inset + 6;
            float lowerWidth = lower.Sum(chip => LinkChatChipWidth(chip) + LINK_CHAT_CHIP_GAP) + 8;
            foreach (Badge chip in rights)
                chip.Visible = minSummary + lowerWidth <= width;
            float bottom = PlaceLinkChatChips(lower, -26);
            float totalsWidth = width - inset - bottom;
            string fit = FitLinkChatText(summary, totals.TextScale, totalsWidth);
            if (!device.Online && fit != summary)
                fit = FitLinkChatText(compactSummary, totals.TextScale, totalsWidth) == compactSummary
                    ? compactSummary : FitLinkChatText(countsSaid, totals.TextScale, totalsWidth);
            totals.Text = UIRichText.Literal(fit);
        }
        return (head, Refresh);
    }

    private Badge LinkChatChip(string text, UIBadgeTone tone = UIBadgeTone.Neutral) =>
        new() { Text = text, Tone = tone, Colour = LinkBadgeColour(tone), TextScale = 0.11f };

    private Vector4? LinkChatDimInk() => Skin is { } skin ? LinkChatReadableInk(skin.DimTextColor) : null;

    private Vector4 LinkChatReadableInk(Vector4 ink, bool chip = false)
    {
        if (Skin is not { } skin)
            return ink;
        Vector4 panel = skin.Palette.Panel;
        Vector4 ground = Vector4.Lerp(new Vector4(0, 0, 0, 1), panel with { W = 1 }, panel.W);
        for (int step = 0; step < 10; step++)
        {
            Vector4 colour = Vector4.Lerp(ink, skin.Palette.Text, step * 0.1f);
            Vector4 behind = chip ? Vector4.Lerp(ground, colour with { W = 1 }, colour.W * 0.16f) : ground;
            if (UIPalette.Contrast(colour, behind) >= 4.5f)
                return colour;
        }
        return skin.Palette.Text;
    }

    private float LinkChatChipWidth(Badge chip)
    {
        if (Skin is not { } skin)
            return 80.0f;
        float line = skin.Font.LineHeight * chip.TextScale;
        float text = skin.Font.Measure(chip.Text, chip.TextScale, markup: false).X;
        float width = 14 + text + (chip.Icon.Length > 0 ? line + 5 : 0) + (chip.Dot ? line * 0.45f + 5 : 0);
        return MathF.Ceiling(MathF.Max(width, MathF.Round(line * 1.45f)));
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

    private Panel ListSectionHead(string key, string title, int count, string flagText, UIBadgeTone flagTone, bool open,
        Action<bool> toggle, Func<Menu>? menu, float inset)
    {
        var row = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, FINISHED_HEAD), Color = Vector4.Zero };
        var head = row.Add(new ChatListHeadButton(this, key, open, toggle)
        {
            Tooltip = Said(key == "local/finished"
                ? open ? "agents_panel.finished_fold" : "agents_panel.finished_open"
                : open ? "link.chats_fold" : "link.chats_open")
        });
        if (menu is not null)
            head.OnContextMenu = at => ContextMenu.Show(chrome, at, menu());
        row.Add(new Label
        {
            Text = $"$({(open ? "chevron-down" : "chevron-right")}) {title}", TextScale = 0.125f, Dim = true, Bold = true,
            Color = key == "local/finished" ? null : LinkChatDimInk(),
            Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(inset, 0), Align = Origin.Left
        });
        string many = count.ToString(CultureInfo.InvariantCulture);
        row.Add(new Badge { Text = many, Tone = UIBadgeTone.Neutral, TextScale = 0.11f, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0) });
        if (flagText.Length > 0)
        {
            // Place the flag to the left of the count, leaving space for the pill padding.
            float countWidth = (Skin?.Font.Measure(many, 0.11f, markup: false).X ?? 8.0f) + 14.0f;
            row.Add(new Badge
            {
                Text = flagText, Tone = flagTone, Dot = true, TextScale = 0.11f,
                Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8 - countWidth - 6, 0)
            });
        }
        return row;
    }

    private UIComponent? ChatListOf(UIComponent from)
    {
        for (UIComponent? at = from; at is not null; at = at.Parent)
        {
            if (at == agentsListRows || at == noonFleetRows)
                return at;
        }
        return null;
    }

    private void RefocusChatListHead(UIComponent list)
    {
        if (linkChatRefocus is not { } focus || focus.List != list)
            return;
        linkChatRefocus = null;
        if (compositor.Focus is { } current && current != focus.Previous)
            return;
        ChatListHeadButton? next = Descendants(list).OfType<ChatListHeadButton>()
            .FirstOrDefault(button => button.Key == focus.Previous.Key && button.IsShownInHierarchy);
        next?.Focus();
    }

    private void MoveChatListFocus(Button from, int step, bool headOnly = false)
    {
        if (ChatListOf(from) is not { } root)
            return;
        List<Button> items = Descendants(root)
            .Where(item => item is AgentCardButton or ChatListHeadButton && item.IsShownInHierarchy && item.EnabledInHierarchy)
            .Cast<Button>().ToList();
        int current = items.IndexOf(from);
        if (current < 0)
            return;
        for (int i = current + step; i >= 0 && i < items.Count; i += step)
        {
            Button next = items[i];
            if (headOnly && next is not ChatListHeadButton)
                continue;
            next.Focus();
            if (root == agentsListRows)
                agentsListScroll.ScrollIntoView(next);
            else
                noonFleetScroll.ScrollIntoView(next);
            return;
        }
    }

    private sealed class ChatListHeadButton : Button
    {
        private readonly DawnScene scene;
        private readonly Action<bool> setOpen;
        internal string Key { get; }
        internal bool Open { get; }

        internal ChatListHeadButton(DawnScene scene, string key, bool open, Action<bool> setOpen)
        {
            this.scene = scene;
            this.setOpen = setOpen;
            Key = key;
            Open = open;
            Style = "button_flat";
            Fill = UIFill.Both;
            Animated = false;
            OnPressed = () => SetOpen(!Open);
        }

        private void SetOpen(bool open)
        {
            if (open == Open)
                return;
            if (IsFocused && scene.ChatListOf(this) is { } list)
                scene.linkChatRefocus = (list, this);
            setOpen(open);
        }

        protected override void OnTextInput(char character)
        {
            switch (character)
            {
                case UIKeyboard.LEFT: SetOpen(false); break;
                case UIKeyboard.RIGHT: SetOpen(true); break;
                case UIKeyboard.UP: scene.MoveChatListFocus(this, -1); break;
                case UIKeyboard.DOWN: scene.MoveChatListFocus(this, 1); break;
                default: base.OnTextInput(character); break;
            }
        }

    }
}
