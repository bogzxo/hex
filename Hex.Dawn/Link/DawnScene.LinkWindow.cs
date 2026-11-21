using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Link's window (docs/hex-link-plan.md, 6): everything Link does in one place. Along the top this device, the name it
// goes by (typed in here, kept in link_name), its fingerprint, how its key is kept (the file alone said as a warning),
// Link's own switch, its port and whether it's found on the network. Then pairing, with its countdown and the addresses
// to type on the other machine, and a box to connect to an address. Then every device this one knows, whether it's
// linked and over which TLS, what it may do here (three switches, Steer in the warning colour with what it means) and
// what this machine may do there, with Connect or Disconnect, Rename and Forget. Then the Hexes heard on the LAN, and
// the latest of the audit log.
//
// The window is made once as it opens and its parts made again only when LinkChanged says something moved, never under
// a held button (a part made again between the press and the letting go lost the click), the two boxes typed into kept
// whole across that. Closed, it costs nothing: nothing listens to LinkChanged and nothing is ticked for it.
internal sealed partial class DawnScene
{
    // The window's size as it opens and the least it goes to
    private static readonly Vector2 LINK_WINDOW_SIZE = new(780, 720), LINK_WINDOW_LEAST = new(560, 420);

    // How many lines of the audit log the window shows
    private const int LINK_ACTIVITY_LINES = 14;

    // The small print of the window's rows and their headings
    private const float LINK_SMALL = 0.125f, LINK_TEXT = 0.135f, LINK_HEADING = 0.15f;

    // How tall the window's buttons and boxes are
    private const float LINK_ROW = 28.0f;

    private WindowPanel? linkWindow;
    private StackPanel? linkWindowHead, linkWindowPairing, linkWindowDevices, linkWindowNearby, linkWindowActivity;
    private TextBox? linkNameBox, linkAddressBox;
    private Label? linkPairingCountdown, linkNameSeen;

    // Whether the window's parts are to be made again, and the second the countdown was last written for
    private bool linkWindowStale;
    private int linkCountdownShown = -1;

    /// <summary>The window in the View menu, as the wiring makes the status item.</summary>
    private void BuildLinkUi()
    {
        commands.Add("View/panels", Said("link.menu"), ShowLinkWindow, icon: "link");
    }

    // A menu of the devices linked now, the one picked cut (and not dialled again until it's connected by hand)
    private void LinkDisconnectCommand()
    {
        List<(string Text, Action Picked)> devices = [.. LinkPeers.Select(peer => (LinkNameOf(peer.Device.Id, peer.Device.Name), (Action)(() => LinkDisconnect(peer.Device.Id))))];
        ChooseFrom(Said("link.disconnect_title"), Said("link.disconnect_text"), devices);
    }

    /// <summary>Link's window brought up, the one up already given the keys rather than another made over it.</summary>
    internal void ShowLinkWindow()
    {
        if (linkWindow is { } open)
        {
            open.TakeKeyboard();
            return;
        }

        var window = new WindowPanel(Said("link.window_title"))
        {
            Icon = "link",
            Size = LINK_WINDOW_SIZE,
            MinSize = LINK_WINDOW_LEAST
        };
        DressWindow(window);
        // Solid, the rows of devices read through glass had the editor behind them showing through
        window.Glass = null;
        window.OnClosed = () =>
        {
            window.Parent?.Remove(window);
            LinkChanged -= LinkWindowChanged;
            linkWindow = null;
            linkWindowHead = linkWindowPairing = linkWindowDevices = linkWindowNearby = linkWindowActivity = null;
            linkNameBox = linkAddressBox = null;
            linkPairingCountdown = linkNameSeen = null;
        };

        var scroll = window.Add(new ScrollPanel { Fill = UIFill.Both, Padding = new UIEdges(18, 14, 12, 16) });
        var column = scroll.Add(new StackPanel { Spacing = 10.0f, Fill = UIFill.Horizontal });

        // The parts made again as Link moves, between the two rows typed into, which are made once
        linkWindowHead = column.Add(new StackPanel { Spacing = 8.0f, Fill = UIFill.Horizontal });
        column.Add(LinkNameRow());
        linkWindowPairing = column.Add(new StackPanel { Spacing = 6.0f, Fill = UIFill.Horizontal });
        column.Add(LinkAddressRow());
        linkWindowDevices = column.Add(new StackPanel { Spacing = 8.0f, Fill = UIFill.Horizontal });
        linkWindowNearby = column.Add(new StackPanel { Spacing = 6.0f, Fill = UIFill.Horizontal });
        linkWindowActivity = column.Add(new StackPanel { Spacing = 2.0f, Fill = UIFill.Horizontal });

        linkWindow = window;
        LinkChanged += LinkWindowChanged;
        chrome.Root.Add(window);
        DrawLinkWindow();
        window.TakeKeyboard();
    }

    // Something of Link's moved, the window's parts made again on this tick or, under a held button, the next one free
    private void LinkWindowChanged() => linkWindowStale = true;

    /// <summary>
    /// Every tick, before anything else of Link's: the window's parts made again when they're stale, the countdown
    /// written again when its second moved, and the status item put right as a device stops steering. Nothing at all
    /// while the window's closed and nothing steers.
    /// </summary>
    private void UpdateLinkUi()
    {
        if (linkSteeringShown && !LinkSteeringNow)
            linkDirty = true;
        UpdateLinkPairingDialog();
        LinkRemoteTick();
        if (link is not null)
            LinkSayGrantsMoved();
        if (linkWindow is null)
            return;

        if (linkWindowStale && !Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left))
        {
            linkWindowStale = false;
            DrawLinkWindow();
        }

        if (linkPairingCountdown is not null && LinkPairingUntil is { } until)
        {
            int left = Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
            if (left != linkCountdownShown)
            {
                linkCountdownShown = left;
                linkPairingCountdown.Text = Said("link.pairing_left", ("time", $"{left / 60}:{(left % 60).ToString("00", CultureInfo.InvariantCulture)}"));
                if (left == 0)
                    linkWindowStale = true;
            }
        }
    }

    // Every part of the window made again from what Link says now
    private void DrawLinkWindow()
    {
        if (linkWindow is null)
            return;
        DrawLinkHead();
        DrawLinkPairing();
        DrawLinkDevices();
        DrawLinkNearby();
        DrawLinkActivity();
        if (linkNameBox is not null && chrome.Compositor.Focus != linkNameBox && linkNameBox.Text != settings.LinkName)
            linkNameBox.Text = settings.LinkName;
        if (linkNameBox is not null)
            linkNameBox.Placeholder = HexLinkNames.Clean(System.Environment.MachineName) ?? string.Empty;
        if (linkNameSeen is not null)
            linkNameSeen.Text = UIRichText.Literal(Said("link.name_seen_as", ("name", LinkDevice?.Name ?? LinkNameWanted)));
        if (linkAddressBox is not null)
            linkAddressBox.Enabled = LinkOn;
    }

    /* This device */

    private void DrawLinkHead()
    {
        StackPanel head = linkWindowHead!;
        ClearRows(head);
        HexLinkDevice? me = LinkDevice;
        string name = me?.Name ?? LinkNameWanted;
        head.Add(WindowSummary(name, Said(LinkOn ? "link.window_on" : "link.window_off"),
            me is null ? Said("link.window_off_text") : Said("link.window_fingerprint", ("fingerprint", me.Fingerprint)), LinkOn ? Skin?.Palette.Success : null));

        // How the key's kept, said plainly, the file alone as a warning
        if (LinkProtection is { } protection)
        {
            string said = Said("link.key_" + protection switch
            {
                HexLinkProtection.Dpapi => "dpapi",
                HexLinkProtection.Keychain => "keychain",
                HexLinkProtection.SecretService => "secret_service",
                _ => "file"
            });
            string icon = protection == HexLinkProtection.File ? "warning" : "lock";
            head.Add(new Label
            {
                Text = protection == HexLinkProtection.File ? $"[color:{HexColour(LinkWarning)}]$({icon}) {UIRichText.Literal(said)}[/color]" : $"$({icon}) {UIRichText.Literal(said)}",
                TextScale = LINK_SMALL, Dim = protection != HexLinkProtection.File, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal
            });
        }

        var switches = head.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 18.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        var on = switches.Add(new ToggleButton(Said("link.switch_on"), LinkOn) { Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("settings_catalog.link_help") });
        on.OnPressed = () => LinkSwitch(on.State);
        var found = switches.Add(new ToggleButton(Said("link.switch_discoverable"), settings.LinkDiscoverable) { Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("settings_catalog.link_discoverable_help") });
        found.OnPressed = () =>
        {
            settings.LinkDiscoverable = found.State;
            SaveSettings();
            SettingsPage?.Refresh("link_discoverable");
            ApplyLink();
        };
        switches.Add(new Label
        {
            Text = LinkPort > 0 ? Said("link.window_port", ("port", LinkPort.ToString(CultureInfo.InvariantCulture))) : Said(LinkOn ? "link.window_dialling" : "link.window_port_off", ("port", settings.LinkPort.ToString(CultureInfo.InvariantCulture))),
            TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = Said("settings_catalog.link_port_help")
        });
        if (LinkNearbyOff is { } off)
            head.Add(new Label { Text = UIRichText.Literal(Said("link.nearby_off", ("why", off))), TextScale = LINK_SMALL, Dim = true, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal });
    }

    // The name this device goes by, typed in and kept in link_name, the machine's own name when it's left empty
    private UIComponent LinkNameRow()
    {
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) };
        row.Add(new Label { Text = Said("link.name_label"), TextScale = LINK_TEXT, Size = new Vector2(120, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        linkNameBox = row.Add(new TextBox
        {
            Text = settings.LinkName, Placeholder = HexLinkNames.Clean(System.Environment.MachineName) ?? string.Empty, MaxLength = HexLinkNames.MOST,
            Size = new Vector2(300, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("settings_catalog.link_name_help")
        });
        void Keep()
        {
            // Cleaned as every device's name is, 40 characters at the most, empty for the machine's own
            string named = HexLinkNames.Clean(linkNameBox?.Text) ?? string.Empty;
            if (linkNameBox is not null && linkNameBox.Text != named)
                linkNameBox.Text = named;
            if (named == settings.LinkName)
                return;
            settings.LinkName = named;
            SaveSettings();
            SettingsPage?.Refresh("link_name");
            ApplyLink();
            LinkTakeName();
            Say(Said("link.renamed_here", ("name", LinkNameWanted)));
        }

        linkNameBox.OnSubmitted = _ => Keep();
        row.Add(new Button { Label = Said("link.name_keep"), Size = new Vector2(80, LINK_ROW), LabelScale = LINK_TEXT, Animated = false, Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = Keep });
        // Back to the machine's own name, the setting emptied
        row.Add(LinkButton(Said("link.name_reset"), "discard", () =>
        {
            if (linkNameBox is not null)
                linkNameBox.Text = string.Empty;
            Keep();
        }));
        var holder = new StackPanel { Spacing = 4.0f, Fill = UIFill.Horizontal };
        holder.Add(row);
        linkNameSeen = holder.Add(new Label { TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal, Padding = new UIEdges(128, 0, 0, 0) });
        return holder;
    }

    /* Pairing and connecting */

    private void DrawLinkPairing()
    {
        StackPanel pairing = linkWindowPairing!;
        ClearRows(pairing);
        linkPairingCountdown = null;
        linkCountdownShown = -1;
        pairing.Add(LinkHeading(Said("link.pair_heading")));
        if (!HexHost.Listens)
        {
            pairing.Add(LinkLine(Said("link.pair_not_here")));
            return;
        }

        var row = pairing.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        bool open = LinkPairingUntil is { } until && until > DateTime.UtcNow;
        row.Add(new Button
        {
            Label = Said(open ? "link.pair_stop" : "link.pair_button"), Icon = open ? "debug-stop" : "add", Primary = !open, Enabled = LinkOn,
            Size = new Vector2(170, LINK_ROW), LabelScale = LINK_TEXT, Animated = false, Anchor = Origin.Left, Pivot = Origin.Left,
            OnPressed = () =>
            {
                if (LinkPairingUntil is not null)
                    LinkClosePairing();
                else
                    LinkPairCommand();
            }
        });
        if (open)
        {
            linkPairingCountdown = row.Add(new Label { Text = string.Empty, TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left });
            // The addresses to type on the other machine, the LAN's first and a VPN's (Tailscale's 100.x) said as such
            string port = LinkPort.ToString(CultureInfo.InvariantCulture);
            List<System.Net.IPAddress> mine = HexLinkAddresses.Preferred(LinkLocalAddresses());
            if (mine.Count == 0)
                pairing.Add(LinkLine(Said("link.pair_addresses_none", ("port", port))));
            else
            {
                pairing.Add(LinkLine(Said("link.pair_addresses")));
                foreach (System.Net.IPAddress address in mine.OrderBy(address => LinkIsVpn(address) ? 1 : 0).Take(6))
                {
                    string shown = $"{HexLinkAddresses.Show(address)}:{port}";
                    pairing.Add(new Label
                    {
                        Text = LinkIsVpn(address) ? $"[b]{shown}[/b]  {UIRichText.Literal(Said("link.address_vpn"))}" : $"[b]{shown}[/b]  {UIRichText.Literal(Said("link.address_lan"))}",
                        TextScale = LINK_TEXT, Align = Origin.Left, Fill = UIFill.Horizontal, Padding = new UIEdges(14, 0, 0, 0)
                    });
                }
            }
        }
        else
            pairing.Add(LinkLine(Said(LinkOn ? "link.pair_text" : "link.pair_off_text")));
    }

    /// <summary>Whether an address is one a VPN hands out (CGNAT's 100.64/10, Tailscale's), said beside it so the LAN's is picked when both work.</summary>
    internal static bool LinkIsVpn(System.Net.IPAddress address)
    {
        byte[] bytes = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }

    // A box for host[:port] and Connect, a known device linked to, another paired with
    private UIComponent LinkAddressRow()
    {
        var holder = new StackPanel { Spacing = 6.0f, Fill = UIFill.Horizontal };
        holder.Add(LinkHeading(Said("link.connect_heading")));
        var row = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        linkAddressBox = row.Add(new TextBox
        {
            Placeholder = Said("link.connect_placeholder"), MaxLength = 200, Size = new Vector2(300, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left,
            Tooltip = Said("link.connect_text"), Enabled = LinkOn
        });
        void Go()
        {
            string typed = linkAddressBox?.Text.Trim() ?? string.Empty;
            if (typed.Length == 0 || !LinkOn)
                return;
            LinkConnectAddress(typed);
        }

        linkAddressBox.OnSubmitted = _ => Go();
        row.Add(new Button { Label = Said("link.connect_button"), Icon = "plug", Size = new Vector2(110, LINK_ROW), LabelScale = LINK_TEXT, Animated = false, Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = Go });
        holder.Add(LinkLine(Said("link.connect_vpn")));
        return holder;
    }

    /* The devices this one knows */

    private void DrawLinkDevices()
    {
        StackPanel devices = linkWindowDevices!;
        ClearRows(devices);
        IReadOnlyList<HexLinkKnown> known = LinkKnown;
        devices.Add(LinkHeading(Said("link.devices_heading", ("count", known.Count.ToString(CultureInfo.InvariantCulture)))));
        if (known.Count == 0)
        {
            devices.Add(LinkLine(Said(LinkOn ? "link.devices_none" : "link.devices_off")));
            return;
        }

        foreach (HexLinkKnown device in known.OrderBy(each => LinkPeerOf(each.Device.Id) is null ? 1 : 0).ThenBy(each => each.Shown, StringComparer.OrdinalIgnoreCase))
            devices.Add(LinkDeviceCard(device));
    }

    /// <summary>The linked peer of a device, null when it isn't linked.</summary>
    internal HexLinkPeer? LinkPeerOf(string id)
    {
        IReadOnlyList<HexLinkPeer> peers = LinkPeers;
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].Device.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !peers[i].IsClosed)
                return peers[i];
        }

        return null;
    }

    // A device as a card: its name and whether it's linked, where, what it may do here and this machine there, its buttons
    private Panel LinkDeviceCard(HexLinkKnown known)
    {
        string id = known.Device.Id;
        HexLinkPeer? peer = LinkPeerOf(id);
        Vector4 tint = LinkDeviceTint(id, Skin);
        Panel card = StripedCard(tint);
        var column = card.Add(new StackPanel { Spacing = 6.0f, Fill = UIFill.Horizontal });

        // Its name with the computer's glyph in its colour, linked or not, and the TLS it's linked over
        var top = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, 22) });
        top.Add(new Label { Text = $"[color:{HexColour(tint)}]$(vm)[/color] [b]{UIRichText.Literal(known.Shown)}[/b]", TextScale = LINK_HEADING, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left });
        top.Add(new Badge
        {
            Text = Said(peer is not null ? "link.device_connected" : LinkHeldOff(id) ? "link.device_held_off" : "link.device_not_connected"),
            Tone = peer is not null ? UIBadgeTone.Success : UIBadgeTone.Neutral, Dot = peer is not null, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left
        });
        if (peer is not null && LinkTlsOf(id) is { } tls)
            top.Add(new Badge { Text = LinkTlsSaid(tls), Icon = "lock", Tone = UIBadgeTone.Neutral, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });

        // What it calls itself, when the person here calls it something else
        if (known.Named is not null)
            column.Add(new Label { Text = UIRichText.Literal(Said("link.device_calls_itself", ("name", known.Device.Name))), TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });

        // Where it is and what it is
        string where = peer?.Session.Connection.Address is { Length: > 0 } address ? address
            : known.Address.Length > 0 ? (known.Port > 0 ? $"{known.Address}:{known.Port.ToString(CultureInfo.InvariantCulture)}" : known.Address) : Said("link.device_no_address");
        string what = string.Join(" · ", new[] { known.Device.Fingerprint, where, peer?.Device.Os is { Length: > 0 } os ? $"{os} {peer.Device.Arch}".Trim() : null }.Where(part => !string.IsNullOrEmpty(part)));
        column.Add(new Label { Text = UIRichText.Literal(what), TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal });

        // What it may do here, a switch each, every change through LinkSetRights and never more than was ticked
        column.Add(new Label { Text = Said("link.device_may_here", ("device", UIRichText.Literal(known.Shown))), TextScale = LINK_SMALL, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal });
        var rights = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 18.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        foreach (HexLinkRights right in (ReadOnlySpan<HexLinkRights>)[HexLinkRights.View, HexLinkRights.Steer, HexLinkRights.Edit])
        {
            bool has = (known.Rights & right) != 0;
            string label = Said("link.right_title_" + HexLinkTrust.RightsNames(right).First());
            // A switch's caption is plain text, Steer's warning colour is in the line under the switches
            var toggle = rights.Add(new ToggleButton(label, has)
            {
                Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("link.right_means_" + HexLinkTrust.RightsNames(right).First())
            });
            HexLinkRights each = right;
            toggle.OnPressed = () =>
            {
                HexLinkRights now = LinkKnown.FirstOrDefault(found => found.Device.Id == id)?.Rights ?? HexLinkRights.None;
                LinkSetRights(id, LinkRightsToggled(now, each, toggle.State));
                linkWindowStale = true;
            };
        }

        // Steer's meaning, never hidden, in the warning colour under the switches
        column.Add(new Label
        {
            Text = $"[color:{HexColour(LinkWarning)}]$(warning)[/color] {UIRichText.Literal(Said("link.right_means_steer"))}", TextScale = LINK_SMALL, Dim = (known.Rights & HexLinkRights.Steer) == 0,
            Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal
        });

        // What this machine may do there, as it says, read-only
        var there = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, 22) });
        there.Add(new Label { Text = Said("link.device_may_there"), TextScale = LINK_SMALL, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left });
        if (peer is null)
            there.Add(new Label { Text = Said("link.device_there_unknown"), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left });
        else
        {
            foreach (Badge chip in LinkRightsChips(peer.Granted))
                there.Add(chip);
        }

        // Connect or Disconnect, Rename, Forget (asked once)
        var buttons = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        if (peer is not null)
            buttons.Add(LinkButton(Said("link.device_disconnect"), "debug-disconnect", () => LinkDisconnect(id)));
        else
            buttons.Add(LinkButton(Said("link.device_connect"), "plug", () => LinkConnectKnown(known))).Enabled = LinkOn && LinkAddressesOf(known, LinkNearby).Count > 0;
        buttons.Add(LinkButton(Said("link.device_rename"), "edit", () => AskText(Said("link.rename_title"), Said("link.rename_text", ("device", known.Device.Name)), known.Shown, Said("link.rename_button"), typed =>
        {
            if (LinkRename(id, typed))
                linkWindowStale = true;
        })));
        if (known.Named is not null)
            buttons.Add(LinkButton(Said("link.device_own_name"), "discard", () => LinkUseOwnName(id)));
        Button forget = buttons.Add(LinkButton(Said("link.device_forget"), "trash", () => AskForgetDevice(known)));
        forget.Danger = true;
        return card;
    }

    /// <summary>
    /// What a device may do after one of its switches was flipped: View is what the others stand on, so ticking Steer or
    /// Edit ticks View with it and taking View away takes them all.
    /// </summary>
    internal static HexLinkRights LinkRightsToggled(HexLinkRights was, HexLinkRights right, bool on)
    {
        if (!on)
            return right == HexLinkRights.View ? HexLinkRights.None : was & ~right;
        return was | right | HexLinkRights.View;
    }

    // Forgetting asked about once, it can't come back without pairing again
    private void AskForgetDevice(HexLinkKnown known)
    {
        string yes = Said("link.forget_go");
        Context.Windows.Ask(Said("link.forget_ask_title", ("device", known.Shown)), Said("link.forget_ask_text", ("device", known.Shown)), [Said("link.pair_cancel"), yes], answer =>
        {
            if (answer == yes)
                LinkForget(known.Device.Id);
        }, risky: yes);
    }

    // A known device dialled at the addresses it was last at and those the LAN says, the first that answers kept
    private void LinkConnectKnown(HexLinkKnown known)
    {
        List<(string Host, int Port)> tries = LinkAddressesOf(known, LinkNearby);
        if (tries.Count == 0)
            return;
        _ = Task.Run(async () =>
        {
            Exception? failed = null;
            foreach (var (host, port) in tries)
            {
                try
                {
                    await LinkConnect(host, port, known.Device.Id);
                    return;
                }
                catch (Exception e)
                {
                    failed = e;
                }
            }

            PostLink(() => Say(Said("link.connect_failed", ("address", $"{tries[0].Host}:{tries[0].Port}"), ("why", failed?.Message ?? "unreachable")), error: true));
        });
    }

    /* The LAN */

    private void DrawLinkNearby()
    {
        StackPanel nearby = linkWindowNearby!;
        ClearRows(nearby);
        IReadOnlyList<HexLinkNearby> found = LinkNearby;
        nearby.Add(LinkHeading(Said("link.nearby_heading")));
        if (!LinkOn || found.Count == 0)
        {
            nearby.Add(LinkLine(Said(LinkOn ? "link.nearby_none" : "link.devices_off")));
            return;
        }

        foreach (HexLinkNearby heard in found)
        {
            HexLinkKnown? known = heard.Id8.Length >= 8 ? LinkKnown.FirstOrDefault(each => each.Device.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase)) : null;
            bool linked = known is not null && LinkPeerOf(known.Device.Id) is not null;
            var row = nearby.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
            string addresses = string.Join(", ", HexLinkAddresses.Preferred(heard.Addresses).Take(2).Select(HexLinkAddresses.Show));
            row.Add(new Label
            {
                Text = $"$(vm) [b]{UIRichText.Literal(HexLinkNames.Clean(heard.Name) ?? heard.Id8)}[/b]  {UIRichText.Literal(addresses)}:{heard.Port.ToString(CultureInfo.InvariantCulture)}",
                TextScale = LINK_TEXT, Size = new Vector2(420, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left
            });
            if (linked)
                row.Add(new Badge { Text = Said("link.device_connected"), Tone = UIBadgeTone.Success, Dot = true, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
            else if (known is not null)
                row.Add(LinkButton(Said("link.device_connect"), "plug", () => LinkConnectKnown(known)));
            else if (HexLinkAddresses.Preferred(heard.Addresses).FirstOrDefault() is { } first)
            {
                string host = HexLinkAddresses.Show(first);
                int port = heard.Port;
                row.Add(LinkButton(Said("link.nearby_pair"), "add", () => _ = LinkPair(host, port)));
            }
        }
    }

    /* What happened */

    private void DrawLinkActivity()
    {
        StackPanel activity = linkWindowActivity!;
        ClearRows(activity);
        activity.Add(LinkHeading(Said("link.activity_heading")));
        List<HexLinkAuditEntry> latest = LinkAuditLatest(LINK_ACTIVITY_LINES);
        if (latest.Count == 0)
        {
            activity.Add(LinkLine(Said("link.activity_none")));
            return;
        }

        for (int i = latest.Count - 1; i >= 0; i--)
        {
            HexLinkAuditEntry entry = latest[i];
            string who = entry.Device.Length == 0 ? string.Empty
                : LinkKnown.FirstOrDefault(each => each.Device.Id.Equals(entry.Device, StringComparison.OrdinalIgnoreCase))?.Shown ?? (entry.Device.Length > 8 ? entry.Device[..8] : entry.Device);
            bool loud = entry.Event is "refused" or "steer" or "edit";
            string line = $"{entry.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  [b]{UIRichText.Literal(entry.Event)}[/b]  {UIRichText.Literal(who)}  {UIRichText.Literal(OneLine(entry.Detail, 160))}";
            activity.Add(new Label
            {
                Text = entry.Event == "refused" ? $"[color:{HexColour(LinkWarning)}]{line}[/color]" : line,
                TextScale = LINK_SMALL, Dim = !loud, Align = Origin.Left, Fill = UIFill.Horizontal, Tooltip = entry.Detail
            });
        }
    }

    /* Pieces */

    // A heading of a part of the window
    private static Label LinkHeading(string text) =>
        new() { Text = $"[b]{UIRichText.Literal(text)}[/b]", TextScale = LINK_HEADING, Align = Origin.Left, Fill = UIFill.Horizontal, Padding = new UIEdges(0, 8, 0, 0) };

    // A quiet line of explanation
    private static Label LinkLine(string text) =>
        new() { Text = UIRichText.Literal(text), TextScale = LINK_SMALL, Dim = true, Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal };

    // A button of a row, as wide as its words
    private Button LinkButton(string label, string icon, Action pressed)
    {
        float wide = Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine($"$({icon}) {label}", LINK_TEXT, Horizon.UI.Drawing.UITextStyle.None, default) + skin.ButtonPadding.Total.X + 14.0f) : 110.0f;
        return new Button { Label = label, Icon = icon, Size = new Vector2(wide, LINK_ROW), LabelScale = LINK_TEXT, Animated = false, Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = pressed };
    }

    /// <summary>What a set of rights is as chips, View, Steer (in the warning tone) and Edit, a quiet "nothing" for none.</summary>
    private static List<Badge> LinkRightsChips(HexLinkRights rights)
    {
        var chips = new List<Badge>();
        if (rights == HexLinkRights.None)
        {
            chips.Add(new Badge { Text = Said("link.rights_none"), Tone = UIBadgeTone.Neutral, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
            return chips;
        }

        foreach (string name in HexLinkTrust.RightsNames(rights))
        {
            chips.Add(new Badge
            {
                Text = Said("link.right_title_" + name), Tone = name == "steer" ? UIBadgeTone.Warning : name == "edit" ? UIBadgeTone.Info : UIBadgeTone.Neutral,
                TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("link.right_means_" + name)
            });
        }

        return chips;
    }

    // TLS's version as people write it
    private static string LinkTlsSaid(System.Security.Authentication.SslProtocols tls) => tls switch
    {
        System.Security.Authentication.SslProtocols.Tls13 => "TLS 1.3",
        System.Security.Authentication.SslProtocols.Tls12 => "TLS 1.2",
        _ => tls.ToString()
    };

    // The warning colour of the look on now
    private Vector4 LinkWarning => Skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);

    /// <summary>
    /// A device's own colour, the same every time for the same device, out of the look's colours that aren't the danger
    /// one, so its badge, its card's stripe and its section of the fleet read as one machine.
    /// </summary>
    internal static Vector4 LinkDeviceTint(string id, UISkin? skin)
    {
        Vector4[] tints =
        [
            RunningBlue(skin),
            skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f),
            AccentText(skin),
            skin?.Palette.LightField == true ? new Vector4(0.55f, 0.32f, 0.75f, 1.0f) : new Vector4(0.74f, 0.58f, 0.98f, 1.0f),
            skin?.Palette.LightField == true ? new Vector4(0.1f, 0.5f, 0.5f, 1.0f) : new Vector4(0.4f, 0.85f, 0.85f, 1.0f)
        ];
        int hash = 0;
        foreach (char c in id)
            hash = unchecked(hash * 31 + char.ToUpperInvariant(c));
        return tints[(hash & 0x7fffffff) % tints.Length];
    }

    /* Steering, for the status item */

    // When a device last asked for something Steer allows here (Environment.TickCount64), and whether the item breathes
    private long linkSteeredAt = long.MinValue / 2;
    private bool linkSteeringShown;

    // How long after a Steer request the status item keeps breathing
    private const long LINK_STEERING_MS = 6000;

    /// <summary>A device asked for something Steer allows here, from the link's thread: the status item breathes a while.</summary>
    private void LinkSteered()
    {
        Interlocked.Exchange(ref linkSteeredAt, System.Environment.TickCount64);
        LinkMoved();
    }

    /// <summary>Whether a device steered something here in the last few seconds.</summary>
    private bool LinkSteeringNow => System.Environment.TickCount64 - Interlocked.Read(ref linkSteeredAt) < LINK_STEERING_MS && LinkPeers.Count > 0;
}
