using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Devices window. Input rows stay mounted as peer state changes around them.
internal sealed partial class DawnScene
{
    // The window's size as it opens and the least it goes to
    private static readonly Vector2 LINK_WINDOW_SIZE = new(780, 640), LINK_WINDOW_LEAST = new(480, 360);

    // How many lines of the audit log the window shows
    private const int LINK_ACTIVITY_LINES = 14;

    // The small print of the window's rows and their headings
    private const float LINK_SMALL = 0.125f, LINK_TEXT = 0.135f, LINK_HEADING = 0.15f;

    // How tall the window's buttons and boxes are
    private const float LINK_ROW = 28.0f;

    private WindowPanel? linkWindow;
    private bool linkActivityShown, linkDeviceRowsNarrow;
    private ScrollPanel? linkDevicesScroll;
    private StackPanel? linkWindowHead, linkWindowDevices, linkWindowNearby, linkWindowActivity;
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
            Size = Vector2.Clamp(chrome.Root.Bounds.Size - new Vector2(30, 60), LINK_WINDOW_LEAST, LINK_WINDOW_SIZE),
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
            linkOnboardingDialog?.Close();
            linkThisDeviceDialog?.Close();
            linkWindowHead = linkWindowDevices = linkWindowNearby = linkWindowActivity = linkWindowShare = linkIdentityWarning = null;
            linkThisDeviceName = linkThisDeviceStatus = null;
            linkKeyFileBadge = null;
            linkThisDeviceSwitch = null;
            linkNameBox = linkAddressBox = null;
            linkPairingCountdown = linkNameSeen = linkJoinStatus = null;
            linkJoinSubmit = linkCopyInvite = null;
            linkDevicesScroll = null;
            linkDevicePage = LinkDevicePage.Overview;
            linkLatencies.Clear();
            linkOfflineRows.Clear();
        };

        var scroll = window.Add(new ScrollPanel { Fill = UIFill.Both, Padding = new UIEdges(18, 14, 12, 16) });
        linkDevicesScroll = scroll;
        var column = scroll.Add(new StackPanel { Spacing = 10.0f, Fill = UIFill.Horizontal });

        linkIdentityWarning = column.Add(new StackPanel { Fill = UIFill.Horizontal });
        linkWindowHead = column.Add(new StackPanel { Spacing = 4, Fill = UIFill.Horizontal });
        BuildLinkHead();
        linkWindowDevices = column.Add(new StackPanel { Spacing = 8, Fill = UIFill.Horizontal });
        linkWindowShare = column.Add(new StackPanel { Spacing = 6, Fill = UIFill.Horizontal });
        linkWindowActivity = column.Add(new StackPanel { Spacing = 2, Fill = UIFill.Horizontal });

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
        {
            LinkSayGrantsMoved();
            TickLinkAutoShare();
            TickLinkWorkspaceOffers();
        }
        if (linkWindow is null)
            return;

        bool narrow = linkWindow.Bounds.Width < 615;
        if (narrow != linkDeviceRowsNarrow)
        {
            linkDeviceRowsNarrow = narrow;
            linkWindowStale = true;
        }
        TickLinkDeviceRows();
        TickLinkInviteCopy();
        if (linkWindowStale && !chrome.Compositor.Pointer.Down)
        {
            linkWindowStale = false;
            DrawLinkWindow();
        }

        if (linkPairingCountdown is not null)
        {
            int left = LinkPairingUntil is { } until ? Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)) : 0;
            if (left != linkCountdownShown)
            {
                linkCountdownShown = left;
                string leftText = UIRichText.Literal(Said("link.pairing_left", ("time", $"{left / 60}:{(left % 60).ToString("00", CultureInfo.InvariantCulture)}")));
                linkPairingCountdown.Text = left < 30 ? $"[color:{HexColour(LinkWarning)}]{leftText}[/color]" : leftText;
                if (left == 0)
                {
                    linkInviteExpired = true;
                    linkWindowStale = true;
                }
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
        DrawLinkShare();
        DrawLinkNearby();
        DrawLinkActivity();
        LinkReadableChips(linkWindow);
        if (linkNameBox is not null && chrome.Compositor.Focus != linkNameBox && linkNameBox.Text != settings.LinkName)
            linkNameBox.Text = settings.LinkName;
        if (linkNameBox is not null)
            linkNameBox.Placeholder = HexLinkNames.Clean(System.Environment.MachineName) ?? string.Empty;
        if (linkNameSeen is not null)
            linkNameSeen.Text = UIRichText.Literal(Said("link.name_seen_as", ("name", LinkDevice?.Name ?? LinkNameWanted)));

    }

    /* This device */

    private Label? linkThisDeviceName, linkThisDeviceStatus;
    private Badge? linkKeyFileBadge;
    private ToggleButton? linkThisDeviceSwitch;
    private UIDialog? linkThisDeviceDialog;

    private void BuildLinkHead()
    {
        StackPanel head = linkWindowHead!;
        StackPanel identity = LinkActionRow(head, 23);
        linkThisDeviceName = identity.Add(new Label { Bold = true, TextScale = LINK_HEADING, Anchor = Origin.Left, Pivot = Origin.Left });
        linkKeyFileBadge = identity.Add(new Badge
        {
            Text = Said("link.devices_key_file_chip"), Icon = "warning", Tone = UIBadgeTone.Warning,
            TextScale = 0.11f, Tooltip = Said("link.key_file"), Anchor = Origin.Left, Pivot = Origin.Left
        });
        linkThisDeviceStatus = head.Add(LinkLine(string.Empty));
        StackPanel actions = LinkActionRow(head, 36);
        var invite = actions.Add(LinkButton(Said("link.devices_invite"), "add", () => ShowLinkDevicePage(LinkDevicePage.Invite)));
        invite.Primary = true;
        invite.Size = invite.Size with { Y = 36 };
        var join = actions.Add(LinkButton(Said("link.devices_join"), "plug", () => ShowLinkDevicePage(LinkDevicePage.Join)));
        join.Size = join.Size with { Y = 36 };
        linkThisDeviceSwitch = actions.Add(new ToggleButton(Said("link.devices_link_switch"), LinkOn)
        {
            Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("link.devices_link_on_help")
        });
        linkThisDeviceSwitch.OnPressed = () => LinkSwitch(linkThisDeviceSwitch.State);
        Button more = actions.Add(LinkButton(Said("link.devices_more"), string.Empty, () => { }));
        more.Tooltip = Said("link.devices_more_this");
        more.OnPressed = () =>
        {
            var menu = new Menu("this device");
            menu.Add(Said("link.devices_identity_details"), ShowLinkIdentityDetails, icon: "edit");
            if (LinkPort != 0)
                menu.Add(Said("link.pair_button"), () =>
                {
                    LinkStartAddressPairing();
                    ShowLinkDevicePage(LinkDevicePage.Invite);
                }, icon: "plug");
            menu.AddSeparator();
            menu.Add(Said(linkActivityShown ? "link.devices_activity_hide" : "link.devices_activity_show"), () =>
            {
                linkActivityShown = !linkActivityShown;
                linkWindowStale = true;
            }, icon: "history");
            ContextMenu.Show(chrome, more.Bounds.PointAt(Origin.TopLeft), menu);
        };
    }

    private void DrawLinkHead()
    {
        if (linkThisDeviceName is null || linkThisDeviceStatus is null || linkThisDeviceSwitch is null)
            return;
        HexLinkDevice? me = LinkDevice;
        linkThisDeviceName.Text = $"[b]{UIRichText.Literal(Said("link.devices_this_device", ("name", me?.Name ?? LinkNameWanted)))}[/b]";
        string state = Said(LinkOn ? "link.devices_on" : "link.devices_off_short");
        string fingerprint = me is null ? string.Empty : Said("link.devices_fingerprint", ("fingerprint", me.Fingerprint));
        string detail = UIRichText.Literal(fingerprint.Length > 0 ? $"{fingerprint} · {state}" : state);
        linkThisDeviceStatus.Text = Skin is { } skin ? $"[color:{HexColour(skin.Palette.DimText)}]{detail}[/color]" : detail;
        linkThisDeviceSwitch.State = LinkOn;
        if (linkKeyFileBadge is not null)
            linkKeyFileBadge.Visible = LinkProtection == HexLinkProtection.File;
        RefreshLinkPublicControls();
        DrawLinkIdentityWarning();
    }

    private StackPanel? linkIdentityWarning;

    private void DrawLinkIdentityWarning()
    {
        StackPanel slot = linkIdentityWarning!;
        ClearRows(slot);
        string? reason = null;
        string? label = null;
        Action? recover = null;
        LinkDeviceIdentityRecovery(ref reason, ref label, ref recover);
        slot.Visible = reason is not null;
        if (!slot.Visible)
            return;
        var panel = slot.Add(new Panel { Fill = UIFill.Horizontal, Radius = 8, Padding = new UIEdges(10, 8), Outline = Skin?.BorderColor ?? Vector4.Zero });
        var body = panel.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 5 });
        body.Add(new Label
        {
            Text = $"[color:{HexColour(LinkWarning)}]$(warning) {UIRichText.Literal(reason ?? string.Empty)}[/color]",
            TextScale = LINK_SMALL, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left
        });
        if (recover is not null && label is not null)
            LinkActionRow(body).Add(LinkButton(label, "warning", recover)).Danger = label == Said("link.devices_identity_reset");
    }

    private void ShowLinkIdentityDetails()
    {
        UIDialog dialog = UIDialog.Show(chrome, Said("link.devices_identity_details"), string.Empty, new DialogChoice(Said("link.devices_done")));
        linkThisDeviceDialog = dialog;
        dialog.Body.Add(LinkNameRow());
        dialog.Body.Add(LinkColourRow());
        if (LinkProtection == HexLinkProtection.File)
            dialog.Body.Add(LinkLine(Said("link.key_file")));
        if (LinkDevice is { } me)
            dialog.Body.Add(LinkLine(Said("link.devices_fingerprint", ("fingerprint", me.Fingerprint))));
        dialog.Body.Add(LinkLine(Said(LinkPort > 0 ? "link.window_port" : LinkOn ? "link.window_dialling" : "link.window_port_off",
            ("port", (LinkPort > 0 ? LinkPort : settings.LinkPort).ToString(CultureInfo.InvariantCulture)))));
        var found = dialog.Body.Add(new ToggleButton(Said("link.switch_discoverable"), settings.LinkDiscoverable)
        {
            Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT, Tooltip = Said("settings_catalog.link_discoverable_help")
        });
        found.OnPressed = () =>
        {
            settings.LinkDiscoverable = found.State;
            SaveSettings();
            SettingsPage?.Refresh("link_discoverable");
            ApplyLink();
        };
        AddLinkPublicControls(dialog.Body);
        dialog.Closed = () =>
        {
            linkPublicStatus = null;
            linkThisDeviceDialog = null;
            linkNameBox = null;
            linkNameSeen = null;
        };
        linkWindowStale = true;
        linkNameBox?.Focus();
    }


    // The colours a device can pick for itself, the presence ones and a few more
    private static readonly string[] LINK_PICKABLE_COLOURS =
        ["#ED8C33", "#59B3F2", "#BF73F2", "#59D98C", "#F2668C", "#F2D94D", "#4DD9D9", "#F2F2F2", "#A6E04D", "#E05A4D"];

    // The colour this device is drawn in on every linked screen, its caret, its dot beside the files it edits, its Link
    // Chat lines. Only this device picks it, kept in link_colour and said with its hello; Automatic is its id's
    private UIComponent LinkColourRow()
    {
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) };
        row.Add(new Label { Text = Said("link.colour_label"), TextScale = LINK_TEXT, Size = new Vector2(75, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        var swatches = new List<Button>();
        void Pick(string colour)
        {
            if (colour == settings.LinkColour)
                return;
            settings.LinkColour = colour;
            foreach (Button each in swatches)
                each.Selected = each.Tooltip == colour;
            SaveSettings();
            SettingsPage?.Refresh("link_colour");
            LinkPickColour(LinkDevice?.Id ?? string.Empty, colour);
            link?.WorkspaceMoved();
        }
        foreach (string colour in LINK_PICKABLE_COLOURS)
        {
            string picked = colour;
            var swatch = new Button
            {
                Size = new Vector2(LINK_ROW, LINK_ROW), Animated = false, Selected = colour == settings.LinkColour,
                Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = colour, OnPressed = () => Pick(picked)
            };
            swatches.Add(row.Add(swatch));
            // A label takes no pointer, the press is the button's
            swatch.Add(new Label { Text = "$(circle-filled)", Anchor = Origin.Center, Pivot = Origin.Center, Align = Origin.Center, TextScale = 0.16f, Color = LinkColourOf(colour) ?? Vector4.One });
        }
        row.Add(LinkButton(Said("link.colour_automatic"), "discard", () => Pick(string.Empty)));
        return row;
    }

    // The name this device goes by, typed in and kept in link_name, the machine's own name when it's left empty
    private UIComponent LinkNameRow()
    {
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) };
        row.Add(new Label { Text = Said("link.name_label"), TextScale = LINK_TEXT, Size = new Vector2(75, LINK_ROW), Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
        linkNameBox = row.Add(new TextBox
        {
            Text = settings.LinkName, Placeholder = HexLinkNames.Clean(System.Environment.MachineName) ?? string.Empty, MaxLength = HexLinkNames.MOST,
            Size = new Vector2(140, LINK_ROW), Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("settings_catalog.link_name_help")
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
        row.Add(new Button { Label = Said("link.name_keep"), Size = new Vector2(55, LINK_ROW), LabelScale = LINK_TEXT, Animated = false, Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = Keep });
        // Back to the machine's own name, the setting emptied
        row.Add(LinkButton(Said("link.name_reset"), "discard", () =>
        {
            if (linkNameBox is not null)
                linkNameBox.Text = string.Empty;
            Keep();
        }));
        var holder = new StackPanel { Spacing = 4.0f, Fill = UIFill.Horizontal };
        holder.Add(row);
        linkNameSeen = holder.Add(LinkLine(Said("link.name_seen_as", ("name", LinkDevice?.Name ?? LinkNameWanted))));
        linkNameSeen.Padding = new UIEdges(83, 0, 0, 0);
        return holder;
    }

    /* The devices this one knows */

    private void DrawLinkDevices()
    {
        StackPanel devices = linkWindowDevices!;
        ClearRows(devices);
        linkOfflineRows.Clear();
        IReadOnlyList<HexLinkKnown> known = LinkKnown;
        devices.Add(LinkHeading(Said("link.devices_counts", ("connected", LinkPeers.Count.ToString(CultureInfo.InvariantCulture)),
            ("total", known.Count.ToString(CultureInfo.InvariantCulture)))));
        if (known.Count == 0)
        {
            var empty = devices.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 5 });
            empty.Add(LinkLine(Said(LinkOn ? "link.devices_none" : "link.window_off_text")));
            LinkActionRow(empty).Add(LinkOn
                ? LinkButton(Said("link.devices_invite"), "add", () => ShowLinkDevicePage(LinkDevicePage.Invite))
                : LinkButton(Said("link.devices_turn_on"), "plug", () => LinkSwitch(true)));
            return;
        }

        IReadOnlyList<LinkConflictShown> conflicts = LinkConflicts;
        foreach (HexLinkKnown device in known.OrderBy(each => LinkPeerOf(each.Device.Id) is not null ? 0 : GetLinkDeviceConnection(each.Device.Id) is { Connecting: true } or { RetryAtUtc: not null } ? 1 : 2)
            .ThenBy(each => each.Shown, StringComparer.OrdinalIgnoreCase))
            devices.Add(LinkDeviceCard(device, conflicts));
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

    /// <summary>
    /// What a device may do after one of its switches was flipped: View is what Steer and Edit stand on, so ticking one of
    /// them ticks View with it and taking View away takes them. Autocomplete (Predict) stands alone, it sees no files:
    /// it neither needs View nor goes with it.
    /// </summary>
    internal static HexLinkRights LinkRightsToggled(HexLinkRights was, HexLinkRights right, bool on)
    {
        if (right == HexLinkRights.Predict)
            return on ? was | HexLinkRights.Predict : was & ~HexLinkRights.Predict;
        if (!on)
            return right == HexLinkRights.View ? was & HexLinkRights.Predict : was & ~right;
        return was | right | HexLinkRights.View;
    }

    /// <summary>Whether what's ticked is enough to pair or to be linked for: View, or Autocomplete on its own.</summary>
    internal static bool LinkRightsAnything(HexLinkRights rights) => (rights & (HexLinkRights.View | HexLinkRights.Predict)) != 0;

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
        string id = known.Device.Id;
        var tries = LinkReconnectAddresses(known);
        if (tries.Length == 0 || linkManualDials.GetValueOrDefault(id)?.Connecting == true)
            return;
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(linkCancel?.Token ?? CancellationToken.None);
        var connecting = new LinkDeviceConnection(true, null, null, attempt);
        linkManualDials[id] = connecting;
        linkWindowStale = true;
        _ = Task.Run(async () =>
        {
            string? reason = null;
            foreach (var (host, port) in tries)
            {
                if (attempt.IsCancellationRequested)
                    break;
                try
                {
                    await LinkConnect(host, port, id, attempt.Token);
                    reason = null;
                    break;
                }
                catch (Exception e)
                {
                    reason = e is HexLinkRefused refusal ? refusal.Reason : OneLine(e.Message, 150);
                }
            }

            PostLink(() =>
            {
                bool current = ReferenceEquals(linkManualDials.GetValueOrDefault(id), connecting);
                bool cancelled = attempt.IsCancellationRequested;
                attempt.Dispose();
                if (!current)
                    return;
                if (reason is null || cancelled)
                    linkManualDials.Remove(id);
                else
                {
                    linkManualDials[id] = new LinkDeviceConnection(false, null, reason);
                    Say(LinkDialFailureSaid(reason), error: true);
                }
                linkWindowStale = true;
            });
        });
    }

    /* The LAN */

    private void DrawLinkNearby()
    {
        if (linkWindowNearby is not { } nearby)
            return;
        ClearRows(nearby);
        nearby.Visible = linkDevicePage == LinkDevicePage.Join;
        if (!nearby.Visible)
            return;
        IReadOnlyList<HexLinkNearby> found = LinkNearby;
        nearby.Add(LinkStep(1, Said("link.devices_join_step_nearby")));
        if (LinkOn && found.Count > 0)
            nearby.Add(LinkLine(Said("link.devices_nearby_hint")));
        if (!LinkOn || found.Count == 0)
        {
            nearby.Add(LinkLine(LinkNearbyOff is { } off ? Said("link.nearby_off", ("why", off)) : Said(LinkOn ? "link.devices_nearby_empty" : "link.pair_off_text")));
            return;
        }

        foreach (HexLinkNearby heard in found)
        {
            HexLinkKnown? known = heard.Id8.Length >= 8 ? LinkKnown.FirstOrDefault(each => each.Device.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase)) : null;
            bool linked = known is not null && LinkPeerOf(known.Device.Id) is not null;
            var row = nearby.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
            string addresses = string.Join(", ", HexLinkAddresses.Preferred(heard.Addresses).Take(2)
                .Select(address => new System.Net.IPEndPoint(address, heard.Port).ToString()));
            row.Add(new Label
            {
                Text = $"$(vm) [b]{UIRichText.Literal(OneLine(HexLinkNames.Clean(heard.Name) ?? heard.Id8, 26))}[/b]",
                TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = addresses
            });
            if (linked)
                row.Add(new Badge { Text = Said("link.device_connected"), Tone = UIBadgeTone.Success, Dot = true, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
            else if (known is not null)
                row.Add(LinkButton(Said("link.device_connect"), "plug", () => LinkConnectKnown(known)));
            else if (HexLinkAddresses.Preferred(heard.Addresses).FirstOrDefault() is { } first)
            {
                string host = HexLinkAddresses.Show(first);
                int port = heard.Port;
                row.Add(LinkButton(Said("link.devices_join_submit"), "add", () => JoinDevice(host, port))).Enabled = !linkJoining;
            }

            if (addresses.Length > 0)
                nearby.Add(LinkLine(addresses));
        }
    }

    /* What happened */

    private void DrawLinkActivity()
    {
        StackPanel activity = linkWindowActivity!;
        ClearRows(activity);
        activity.Visible = linkActivityShown;
        if (!linkActivityShown)
            return;
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
        string shown = icon.Length > 0 ? $"$({icon}) {label}" : label;
        float wide = Skin is { } skin ? MathF.Ceiling(skin.Font.MeasureLine(shown, LINK_TEXT, Horizon.UI.Drawing.UITextStyle.None, default) + skin.ButtonPadding.Total.X + 14.0f) : 110.0f;
        return new Button { Label = label, Icon = icon, Size = new Vector2(wide, LINK_ROW), LabelScale = LINK_TEXT, Animated = false, Anchor = Origin.Left, Pivot = Origin.Left, OnPressed = pressed };
    }

    /// <summary>What a set of rights is as chips, View, Steer (in the warning tone) and Edit, a quiet "nothing" for none.</summary>
    private List<Badge> LinkRightsChips(HexLinkRights rights)
    {
        var chips = new List<Badge>();
        if (rights == HexLinkRights.None)
        {
            chips.Add(new Badge { Text = Said("link.rights_none"), Tone = UIBadgeTone.Neutral, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
            return chips;
        }

        foreach (string name in HexLinkTrust.RightsNames(rights))
        {
            var chip = new Badge
            {
                Text = Said("link.right_title_" + name), Icon = name == "steer" ? "warning" : string.Empty,
                Tone = name == "steer" ? UIBadgeTone.Warning : name is "edit" or "predict" ? UIBadgeTone.Info : UIBadgeTone.Neutral,
                TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("link.right_means_" + name)
            };
            if (chip.Tone == UIBadgeTone.Info && Skin is { Palette.LightField: true } skin)
                chip.Colour = Vector4.Lerp(chip.ColourIn(skin), skin.Palette.Text, 0.55f);
            chips.Add(chip);
        }

        return chips;
    }

    private Vector4? LinkBadgeInfoColour => Skin is { Palette.LightField: true } skin ? skin.Palette.Link : null;

    /// <summary>
    /// The colour a chip of a tone is drawn in so its words read at 4.5:1 or better. The light looks put their tone
    /// colours on tinted chips at about 4:1, mixed a half toward the text colour they pass; the dark ones already do.
    /// </summary>
    private Vector4? LinkBadgeColour(UIBadgeTone tone)
    {
        if (Skin is not { } skin)
            return null;
        bool light = skin.Palette.LightField;
        Vector4? basis = tone switch
        {
            UIBadgeTone.Success => skin.Palette.Success,
            UIBadgeTone.Warning => skin.Palette.Warning,
            UIBadgeTone.Danger => skin.Palette.Error,
            UIBadgeTone.Info => light ? skin.Palette.Link : new Vector4(0.38f, 0.62f, 1.0f, 1.0f),
            _ => null
        };
        return basis is { } colour ? Vector4.Lerp(colour, skin.Palette.Text, light ? 0.5f : 0.4f) : null;
    }

    // Every chip of the window put right for the look, the ones given a colour of their own left as they are
    private void LinkReadableChips(UIComponent from)
    {
        foreach (UIComponent child in from.Children)
        {
            if (child is Badge { Colour: null } badge)
                badge.Colour = LinkBadgeColour(badge.Tone);
            LinkReadableChips(child);
        }
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
    /// A device's own colour, the one its caret is drawn in (DawnScene.LinkPresence), so its badge, its card's stripe, its
    /// section of the fleet and where its person is read as one machine.
    /// </summary>
    internal static Vector4 LinkDeviceTint(string id, UISkin? skin) => LinkDeviceColour(id);

    // Worker E supplies a reason and an explicit recovery action when its identity state lands.
    partial void LinkDeviceIdentityRecovery(ref string? reason, ref string? label, ref Action? recovery);

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
