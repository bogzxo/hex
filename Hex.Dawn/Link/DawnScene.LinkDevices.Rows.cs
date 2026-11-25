using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkDeviceConnection(bool Connecting, DateTime? RetryAtUtc, string? Reason, CancellationTokenSource? Attempt = null,
        string? Detail = null, string? Route = null, bool Reconnecting = false);
    private Dictionary<string, LinkDeviceConnection> linkManualDials { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private Dictionary<string, (HexLinkPeer Peer, long Sampled, double? Milliseconds)> linkLatencies { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
    private List<(Badge State, Label Details, HexLinkKnown Device, bool HasAddress)> linkOfflineRows { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }

    private (int Version, long Second, int Rows) linkOfflineStamp;

    // A device's card: its name and state with Disconnect and More at the right, then what is shared, what each may do and
    // anything that needs the person. The rarer actions are in More, each action in one place only
    private Panel LinkDeviceCard(HexLinkKnown known, IReadOnlyList<LinkConflictShown> conflicts)
    {
        string id = known.Device.Id;
        HexLinkPeer? peer = LinkPeerOf(id);
        bool hasAddress = peer is not null || LinkReconnectAddresses(known).Length > 0;
        Vector4 tint = LinkDeviceTint(id, Skin);
        Panel card = StripedCard(tint);
        var column = card.Add(new StackPanel { Spacing = 6, Fill = UIFill.Horizontal });

        var head = column.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, LINK_ROW) });
        var left = head.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 12, Anchor = Origin.Left, Pivot = Origin.Left });
        left.Add(new Label
        {
            Text = $"[color:{HexColour(tint)}]$(vm)[/color] [b]{UIRichText.Literal(OneLine(LinkNameOf(known.Device.Id, known.Device.Name), 26))}[/b]",
            TextScale = LINK_HEADING, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = LinkNameOf(known.Device.Id, known.Device.Name)
        });
        var badge = left.Add(new Badge { TextScale = LINK_SMALL, Anchor = Origin.Left, Pivot = Origin.Left });
        var right = head.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Anchor = Origin.Right, Pivot = Origin.Right });
        if (peer is not null)
            right.Add(LinkButton(Said("link.devices_disconnect_now"), "debug-disconnect", () => LinkDisconnect(id)))
                .Tooltip = Said("link.devices_disconnect_now_tip", ("device", known.Shown));
        Button more = right.Add(LinkButton(Said("link.devices_more"), string.Empty, () => { }));
        more.Tooltip = Said("link.devices_more_other", ("device", known.Shown));

        if (peer is null)
        {
            Label details = column.Add(LinkLine(string.Empty));
            linkOfflineRows.Add((badge, details, known, hasAddress));
            DrawLinkOfflineRow(badge, details, known, hasAddress);
        }
        else
        {
            bool paused = LinkDevicePauseOf(peer).IsPaused;
            bool measured = linkLatencies.TryGetValue(id, out var sampled) && sampled.Peer == peer && sampled.Milliseconds is not null;
            badge.Text = paused ? Said("link.device_paused_all")
                : measured ? Said("link.devices_status_connected_ms", ("ms", Math.Max(1, Math.Round(sampled.Milliseconds!.Value)).ToString(CultureInfo.InvariantCulture)))
                : Said("link.devices_status_connected");
            badge.Tone = paused || sampled.Milliseconds > 150 ? UIBadgeTone.Warning : UIBadgeTone.Success;
            badge.Dot = true;
            badge.Tooltip = LinkRouteTip(known, peer);
        }

        // What needs the person leads the card, under the name and the state, so it isn't found at the bottom
        LinkConflictShown[] forDevice = [.. conflicts.Where(conflict => conflict.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase))];
        if (forDevice.Length > 0)
        {
            var lead = LinkActionRow(column, LINK_ROW);
            lead.Add(new Label
            {
                Text = $"[color:{HexColour(LinkWarning)}]$(warning) {UIRichText.Literal(Said("link.devices_lead_conflicts", ("count", forDevice.Length.ToString(CultureInfo.InvariantCulture))))}[/color]",
                TextScale = LINK_TEXT, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left
            });
            lead.Add(LinkButton(Said("link.devices_resolve", ("count", forDevice.Length.ToString(CultureInfo.InvariantCulture))),
                "warning", () => LinkChooseConflict(forDevice))).Primary = true;
        }

        if (known.Named is not null)
            column.Add(LinkLine(Said("link.device_calls_itself", ("name", known.Device.Name))));

        if (peer is not null && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
        {
            HexLinkWorkspace workspace = peer.Workspace;
            // The workspace's name isn't said on its own line: its folders are the rows below, with their own names
            if (workspace.Name.Length == 0 && workspace.Folders.Count == 0)
                column.Add(LinkLine(Said("link.devices_workspace_empty")));
            foreach (HexLinkRepository repository in workspace.Repositories)
                column.Add(LinkWorkspaceCommit(repository));
        }

        if (peer is not null)
            LinkDeviceSyncRows(column, peer);

        // One plain sentence for what each side may do, the detail behind Change
        // Wrapped to the card's width, Change under it so it can't be cut off at the edge
        // A device that was disconnected says that and Connect, not what it "can" do: that read as a contradiction
        if (peer is not null || !LinkHeldOff(id))
            column.Add(new Label
            {
                Text = UIRichText.Literal(LinkRightsSentence(known, peer)), TextScale = LINK_TEXT, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.TopLeft
            });
        if (peer is not null)
            LinkActionRow(column).Add(LinkButton(Said("link.devices_change"), string.Empty, () => LinkChangeDeviceRights(known))).Tooltip = Said("link.tip_edit_permissions");
        if (peer is not null)
            DrawLinkClaimCount(column, id);

        DrawLinkPortsRows(column, known, peer);
        DrawLinkDeviceActions(column, known, peer, hasAddress, more);
        return card;
    }

    // Where it is reached and how: the tooltip of the state
    private string LinkRouteTip(HexLinkKnown known, HexLinkPeer peer)
    {
        string text = LinkTlsOf(known.Device.Id) is { } tls ? LinkTlsSaid(tls) : string.Empty;
        string address = peer.Session.Connection.Address;
        if (address.Length == 0)
            return text;
        bool knownIp = IPAddress.TryParse(address, out IPAddress? ip);
        bool vpn = ip is not null && LinkIsVpn(ip);
        string route = Said(!knownIp ? "link.tip_route_network" : vpn ? "link.tip_route_vpn" : "link.tip_route") + " " + Said("link.devices_route_tip", ("address", address),
            ("choice", known.PreferredRoute is { } routeChoice ? LinkRouteAddress(routeChoice) : Said("link.devices_route_auto")));
        return text.Length > 0 ? text + "\n" + route : route;
    }

    // "Desk-A can view and edit your files. You can view and edit theirs." for a connected device, its half alone otherwise
    private string LinkRightsSentence(HexLinkKnown known, HexLinkPeer? peer)
    {
        string theirs = LinkPlainRights(known.Rights, yours: true);
        string device = known.Shown;
        string first = theirs.Length > 0 ? Said("link.plain_they_can", ("device", device), ("things", theirs)) : Said("link.plain_they_cannot", ("device", device));
        if (peer is null)
            return first;
        string mine = LinkPlainRights(peer.Granted, yours: false);
        return first + " " + (mine.Length > 0 ? Said("link.plain_you_can", ("things", mine)) : Said("link.plain_you_cannot"));
    }

    /// <summary>What a set of rights lets somebody do, in plain words: "view and edit your files and use your AI agents".</summary>
    internal static string LinkPlainRights(HexLinkRights rights, bool yours)
    {
        string owner = Said(yours ? "link.plain_owner_yours" : "link.plain_owner_theirs");
        var things = new List<string>(4);
        bool view = HexLinkAllowed.Has(rights, HexLinkRights.View), edit = HexLinkAllowed.Has(rights, HexLinkRights.Edit);
        if (view && edit)
            things.Add(Said("link.plain_view_edit_files", ("owner", owner)));
        else if (view)
            things.Add(Said("link.plain_view_files", ("owner", owner)));
        else if (edit)
            things.Add(Said("link.plain_edit_files", ("owner", owner)));
        if (HexLinkAllowed.Has(rights, HexLinkRights.Steer))
            things.Add(Said("link.plain_steer", ("owner", owner)));
        if (HexLinkAllowed.Has(rights, HexLinkRights.Predict))
            things.Add(Said("link.plain_predict", ("owner", owner)));
        if (HexLinkAllowed.Has(rights, HexLinkRights.Ports))
            things.Add(Said("link.plain_ports", ("owner", owner)));
        if (HexLinkAllowed.Has(rights, HexLinkRights.Ai))
            things.Add(Said("link.plain_ai", ("owner", owner)));
        return things.Count switch
        {
            0 => string.Empty,
            1 => things[0],
            _ => string.Join(", ", things.Take(things.Count - 1)) + Said("link.plain_and") + things[^1]
        };
    }

    private (string Host, int Port)[] LinkReconnectAddresses(HexLinkKnown known) =>
        [.. LinkDialRoutes(known, LinkNearby, linkRedial.Addresses(known.Device.Id), linkRedial.LastLinked(known.Device.Id)).Select(route => (route.Host, route.Port))];

    private void LinkChangeDeviceRights(HexLinkKnown known)
    {
        HexLinkRights grants = known.Rights;
        HexLinkPreApproval preApproval = known.RemotePreApproval;
        UIDialog dialog = UIDialog.Show(chrome, UIRichText.Literal(Said("link.pair_grant", ("device", known.Shown))), string.Empty,
            new DialogChoice(Said("link.devices_save"), () =>
            {
                if (preApproval != known.RemotePreApproval)
                    linkTrust?.SetRemotePreApproval(known.Device.Id, preApproval);
                LinkSetRights(known.Device.Id, grants);
            }),
            new DialogChoice(Said("link.pair_cancel")));
        dialog.Body.Size = new Vector2(Math.Clamp(chrome.Root.Bounds.Width - 80, 350, 460), 0);
        HexLinkPreset preset = (grants & (HexLinkRights.Ports | HexLinkRights.Ai)) != 0 ? HexLinkPreset.Custom : (grants & ~LINK_EXTRAS) switch
        {
            HexLinkRights.View => HexLinkPreset.Viewer,
            HexLinkRights.View | HexLinkRights.Edit => HexLinkPreset.Collaborator,
            HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit => HexLinkPreset.Worker,
            _ => HexLinkPreset.Custom
        };
        LinkGrantControls(dialog.Body, preset, grants, (_, changed) => grants = changed);
        dialog.Body.Add(LinkLine(Said("link.devices_preapproval_heading", ("device", known.Shown))));
        StackPanel choices = LinkActionRow(dialog.Body);
        var buttons = new Dictionary<HexLinkPreApproval, Button>();
        Label warning = dialog.Body.Add(new Label
        {
            Text = Said("link.devices_preapproval_warning"), TextScale = LINK_SMALL,
            Wrap = true, Align = Origin.Left, Fill = UIFill.Horizontal,
            Visible = preApproval == HexLinkPreApproval.Everything
        });
        foreach (HexLinkPreApproval policy in Enum.GetValues<HexLinkPreApproval>())
        {
            Button button = choices.Add(LinkButton(LinkPreApprovalName(policy), string.Empty, () =>
            {
                buttons[preApproval].Primary = false;
                preApproval = policy;
                buttons[preApproval].Primary = true;
                warning.Visible = preApproval == HexLinkPreApproval.Everything;
            }));
            button.Primary = policy == preApproval;
            buttons.Add(policy, button);
        }
    }

    private static string LinkPreApprovalName(HexLinkPreApproval policy) =>
        Said("link.devices_preapproval_" + HexLinkSpawnPolicy.Word(policy));

    private static string LinkPreApprovalLabel(HexLinkPreApproval policy, string device) =>
        Said("link.devices_preapproval", ("device", device), ("policy", LinkPreApprovalName(policy).ToLowerInvariant()));

    private static void LinkRightsCaption(StackPanel column, string caption, string tip)
    {
        var label = column.Add(LinkLine(caption));
        label.Bold = true;
        label.Dim = false;
        label.Tooltip = tip;
    }

    private Label LinkWorkspaceCommit(HexLinkRepository repo)
    {
        HexLinkRepository? local = linkServices?.Workspace.Repositories.FirstOrDefault(each => each.Name.Equals(repo.Name, StringComparison.OrdinalIgnoreCase));
        string comparison = repo.Head.Length == 0 || local is not { Head.Length: > 0 } ? Said("link.devices_commit_unknown")
            : Said(local.Head.Equals(repo.Head, StringComparison.OrdinalIgnoreCase) ? "link.devices_commit_same" : "link.devices_commit_different");
        string head = repo.Head[..Math.Min(repo.Head.Length, 8)];
        string branch = repo.Branch.Length > 0 ? OneLine(repo.Branch, 24) : Said("link.devices_commit_detached");
        return LinkLine(Said("link.devices_commit", ("repo", OneLine(repo.Name, 45)), ("branch", branch), ("head", head), ("state", comparison)));
    }

    // What the card does besides what its rows do: the offer waiting, how to connect when it isn't, and the More menu
    private void DrawLinkDeviceActions(StackPanel column, HexLinkKnown known, HexLinkPeer? peer, bool hasAddress, Button more)
    {
        string id = known.Device.Id;
        LinkWorkspaceOffer? pendingOffer = linkWorkspaceOffers.GetValueOrDefault(id);
        bool canGetOffer = CanGetLinkOffer(peer);
        if (pendingOffer is not null && (peer is null || canGetOffer))
        {
            // The offer as a row of the card: who shares what, and the button that asks where to put it
            var offerRow = LinkActionRow(column);
            offerRow.Add(new Label { Text = UIRichText.Literal(LinkWorkspaceOfferTitle(pendingOffer)), TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left });
            if (peer is not null)
            {
                var offered = pendingOffer;
                offerRow.Add(LinkButton(Said("link.devices_offer_get", ("workspace", offered.Name)), "cloud-download", () => GetLinkOfferedWorkspace(offered)));
                if (LinkHasFolderNamed(offered.Name))
                    column.Add(LinkLine(Said("link.devices_offer_have", ("workspace", offered.Name))));
            }
            else
                column.Add(LinkLine(Said("link.devices_workspace_offer_reconnect")));
        }

        if (peer is not null)
            LinkAiStatusRow(column, id);
        Action? share = null, get = null;
        bool offerShare = !(pendingOffer is not null && canGetOffer) && linkServices?.Workspace.Folders.Count > 0;
        if (peer is not null)
        {
            LinkDeviceProvisionActions(id, ref share, ref get);
            if (pendingOffer is not null && canGetOffer)
                get = () => GetLinkOfferedWorkspace(pendingOffer);
            if ((offerShare ? share : get) is null)
                column.Add(LinkLine(Said("link.devices_provision_short")));
        }
        else
        {
            var actions = LinkActionRow(column);
            LinkDeviceConnection? state = GetLinkDeviceConnection(id);
            if (state is { Attempt: not null, Connecting: true })
                actions.Add(LinkButton(Said("link.pair_cancel"), "debug-stop", () => LinkDisconnect(id)));
            else if (state?.Reason is "refused" or "pin" or "unknown")
                actions.Add(LinkButton(Said("link.devices_pair_again"), "link", () => ShowLinkDevicePage(LinkDevicePage.Join)));
            else if (state?.Reason != "version")
            {
                string key = state?.RetryAtUtc is not null || state?.Reason is "unreachable" or "timeout" or "firewall" ? "link.devices_try_now" : "link.device_connect";
                if (LinkOn && state?.Connecting != true && !hasAddress)
                {
                    actions.Add(LinkButton(Said("link.devices_join_from_invite"), "link", () => ShowLinkDevicePage(LinkDevicePage.Join)));
                    column.Add(LinkLine(Said("link.devices_no_reconnect")));
                }
                else
                    AddLinkAction(actions, key, "plug", LinkOn && state?.Connecting != true ? () => LinkConnectKnown(known) : null,
                        LinkOn ? "link.devices_connecting" : "link.pair_off_text", known.Shown);
            }
        }

        // Grouped as the person thinks of it: how it is reached, what is shared, what it is called, then the ways to end it
        more.OnPressed = () =>
        {
            var menu = new Menu("device actions");
            // Read as the menu opens, the card may have been drawn a moment before a sync changed
            bool sharing = LinkSyncs.Any(sync => sync.DeviceId == id && sync.State != HexLinkSyncState.Off);
            LinkAddRouteMenu(menu, known);
            if (peer is not null)
            {
                menu.AddSeparator();
                Action? first = offerShare ? share : get, second = offerShare ? get : share;
                if (first is not null)
                    menu.Add(Said(offerShare ? "link.devices_share" : "link.devices_get", ("device", known.Shown)), first, icon: offerShare ? "cloud-upload" : "cloud-download");
                if (second is not null)
                    menu.Add(Said(offerShare ? "link.devices_get" : "link.devices_share", ("device", known.Shown)), second, icon: offerShare ? "cloud-download" : "cloud-upload");
                if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer))
                    menu.Add(Said("link.devices_start_agent", ("device", known.Shown)), () => LinkStartAgentOnDevice(id), icon: "robot");
                if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                    menu.Add(Said("link.devices_open_files", ("device", known.Shown)), () =>
                    {
                        CloseLinkWindow();
                        LinkOpenRemoteCommand(peer);
                    }, icon: "folder-opened");
                if (LinkPortsOf is not null)
                    menu.Add(Said("link.ports_share"), () => LinkAskShare(id), icon: "add");
                List<string> folders = LinkSharedFolders(peer);
                foreach (string folder in folders)
                {
                    if (LinkTryChangesSafely(folder) is { } safely)
                        menu.Add(folders.Count > 1 ? $"{Said("link.devices_try_safely")} ({folder})" : Said("link.devices_try_safely"), safely, icon: "git-branch");
                }

                HexLinkDevicePause pause = LinkDevicePauseOf(peer);
                if (pause.CanPause)
                    menu.Add(Said("link.devices_pause_all_menu"), () => LinkPauseDevice(id, pause), icon: "debug-pause");
                else if (pause.CanResume && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                    menu.Add(Said("link.devices_resume_all"), () => LinkPauseDevice(id, pause), icon: "sync");
                if (sharing)
                    menu.Add(Said("link.devices_stop_sharing"), () => LinkStopDeviceSharing(known), icon: "debug-stop");
            }

            menu.AddSeparator();
            menu.Add(Said("link.device_rename"), () => AskText(Said("link.rename_title"), Said("link.rename_text", ("device", known.Device.Name)),
                known.Shown, Said("link.rename_button"), typed => LinkRename(id, typed)), icon: "edit");
            if (known.Named is not null)
                menu.Add(Said("link.device_own_name"), () => LinkUseOwnName(id), icon: "discard");
            menu.Add(Said("link.devices_details"), () => UIDialog.Show(chrome, known.Shown, Said("link.devices_device_id", ("fingerprint", known.Device.Fingerprint)) + "\n" +
                LinkPlatform(peer?.Device ?? known.Device), new DialogChoice(Said("link.devices_done"))), icon: "info");
            menu.AddSeparator();
            menu.Add(Said("link.device_forget"), () => AskForgetDevice(known), icon: "trash");
            // Under the button, not over it: opened at its corner the list covered the button and the release of the same
            // press landed on an item of it, picking it or shutting the list at once
            ContextMenu.Show(chrome, more.Bounds.PointAt(Origin.BottomLeft), menu);
        };
    }

    private static string LinkPlatform(HexLinkDevice device)
    {
        string os = device.Os switch
        {
            "windows" => "Windows",
            "linux" => "Linux",
            "macos" => "macOS",
            _ => OneLine(device.Os, 40)
        };
        return $"{os} {device.Arch}".Trim();
    }

    private void AddLinkAction(StackPanel row, string labelKey, string icon, Action? action, string unavailable, string device = "", string tipKey = "")
    {
        Button button = row.Add(LinkButton(Said(labelKey, ("device", device)), icon, () => action?.Invoke()));
        if (action is not null && tipKey.Length > 0)
            button.Tooltip = Said(tipKey);
        button.Enabled = action is not null;
        if (action is null)
            button.Tooltip = Said(unavailable);
    }

    private void LinkStopDeviceSharing(HexLinkKnown known)
    {
        string stop = Said("link.devices_stop_sharing");
        Context.Windows.Ask(Said("link.devices_stop_share_title", ("device", known.Shown)), Said("link.devices_stop_share_text"),
            [Said("link.pair_cancel"), stop], answer =>
            {
                if (answer != stop)
                    return;
                foreach (LinkSyncShown sync in LinkSyncs.Where(each => each.DeviceId.Equals(known.Device.Id, StringComparison.OrdinalIgnoreCase)))
                    LinkSyncStop(known.Device.Id, sync.Folder);
                linkWindowStale = true;
            }, risky: stop);
    }

    // The files in conflict picked from, file by file, the Devices window put away first
    private void LinkChooseConflict(IReadOnlyList<LinkConflictShown> conflicts) => LinkPickConflict(conflicts);

    private LinkDeviceConnection? GetLinkDeviceConnection(string id)
    {
        LinkDeviceConnection? state = null;
        LinkDeviceRedial(id, ref state);
        return linkManualDials.TryGetValue(id, out LinkDeviceConnection? manual) && (manual.Connecting || state is null) ? manual : state;
    }

    private void DrawLinkOfflineRow(Badge badge, Label details, HexLinkKnown known, bool hasAddress)
    {
        string id = known.Device.Id;
        LinkDeviceConnection? state = LinkOn ? GetLinkDeviceConnection(id) : null;
        string? reason = state?.Reason;
        string key = state switch
        {
            // Dropped this session and being dialled again, whatever the last dial said, rather than plain Offline
            { Reconnecting: true } => "link.device_reconnecting",
            { Connecting: true } => "link.devices_connecting",
            { RetryAtUtc: not null } => "link.devices_retrying",
            _ when reason is "version" => "link.devices_version",
            _ when reason is "refused" or "pin" or "unknown" => "link.devices_refused",
            _ when reason is "unreachable" or "timeout" or "firewall" => "link.devices_unreachable",
            _ when LinkHeldOff(id) && LinkHeldOffBy(id) is not null => "link.device_held_by",
            _ when LinkHeldOff(id) => "link.device_held_off",
            _ => "link.device_not_connected"
        };
        string seconds = state?.RetryAtUtc is { } retry ? Math.Max(0, (int)Math.Ceiling((retry - DateTime.UtcNow).TotalSeconds)).ToString(CultureInfo.InvariantCulture) : "0";
        string text = Said(key, ("seconds", seconds), ("device", OneLine(LinkHeldOffBy(id) ?? string.Empty, 30)));
        if (badge.Text != text)
            badge.Text = text;
        badge.Dot = true;
        badge.Tone = state?.Connecting == true || state?.Reconnecting == true ? UIBadgeTone.Info : key == "link.devices_refused" ? UIBadgeTone.Danger
            : reason is not null ? UIBadgeTone.Warning : UIBadgeTone.Neutral;
        badge.Colour = LinkBadgeColour(badge.Tone);
        string action = reason is null
            ? Said(!LinkOn ? "link.pair_off_text" : LinkHeldOffBy(id) is not null ? "link.devices_held_by_help" : LinkHeldOff(id) ? "link.devices_held_help" : "link.devices_offline_help")
            : LinkDialFailureSaid(reason, state?.Detail);
        string trying = state?.Route ?? (known.PreferredRoute is { } route ? LinkRouteAddress(route) : known.Address);
        if (state?.Connecting == true || state is { Reconnecting: true, RetryAtUtc: null })
            action = Said("link.devices_trying_route", ("address", trying));
        else if (state?.RetryAtUtc is not null)
            action = hasAddress ? Said("link.devices_retry_help", ("why", action)) : Said("link.devices_retry_no_address");
        else if (key == "link.device_not_connected" && LinkOn)
            action = Said("link.devices_last_seen", ("time", known.Seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), ("next", action));
        string escaped = UIRichText.Literal(action);
        if (details.Text != escaped)
            details.Text = escaped;
        details.Visible = true;

        // What a reconnecting device's last dial said in full, the address being tried and when the next try is, for the
        // person who wants to know why it hasn't come back
        if (state?.Reconnecting == true)
        {
            string tip = string.Join('\n', new[]
            {
                state.Detail is { } failure ? Said("link.devices_last_failure", ("why", OneLine(failure, 200))) : null,
                Said("link.devices_trying_route", ("address", trying)),
                state.RetryAtUtc is not null ? Said("link.devices_retrying", ("seconds", seconds)) : null
            }.OfType<string>());
            badge.Tooltip = tip;
        }
        else if (badge.Tooltip.Length > 0)
            badge.Tooltip = string.Empty;
    }

    private static string LinkDialFailureSaid(string reason, string? detail = null) => reason switch
    {
        "version" => Said("link.devices_version_help"),
        "route" => Said("link.devices_route_refused"),
        "refused" or "pin" or "unknown" => Said("link.devices_refused_help"),
        "unreachable" or "timeout" or "firewall" => Said("link.devices_firewall_help"),
        _ => Said("link.devices_dial_help", ("why", OneLine(detail ?? reason, 150).TrimEnd('.')))
    };

    private void TickLinkDeviceRows()
    {
        long now = System.Environment.TickCount64;

        // The offline rows say words and a countdown in whole seconds: made again when Link moved or the second did, not every frame
        (int Version, long Second, int Rows) stamp = (LinkVersion, now / 1000, linkOfflineRows.Count);
        if (stamp != linkOfflineStamp)
        {
            linkOfflineStamp = stamp;
            foreach (var (badge, details, device, hasAddress) in linkOfflineRows)
                DrawLinkOfflineRow(badge, details, device, hasAddress);
        }

        foreach (HexLinkPeer peer in LinkPeers)
        {
            string id = peer.Device.Id;
            if (linkLatencies.TryGetValue(id, out var old) && old.Peer == peer && now - old.Sampled < 15000)
                continue;
            linkLatencies[id] = (peer, now, old.Peer == peer ? old.Milliseconds : null);
            _ = LinkMeasureLatency(peer, now);
            if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                _ = TryRefreshLinkWorkspace(peer);
        }
    }

    private async Task LinkMeasureLatency(HexLinkPeer peer, long sampled)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await peer.Session.Ask("link/ping", timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            PostLink(() =>
            {
                string id = peer.Device.Id;
                if (linkWindow is null || !linkLatencies.TryGetValue(id, out var current) || current.Peer != peer || current.Sampled != sampled)
                    return;
                linkLatencies[id] = (peer, sampled, ms);
                linkWindowStale = true;
            });
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException or HexLinkException)
        {
            // The connection monitor supplies the failure state. No latency sample was received.
        }
    }

    // Implemented by the provisioning and redial integrations when their commands and models are present.
    partial void LinkDeviceProvisionActions(string deviceId, ref Action? share, ref Action? get);
    partial void LinkDeviceRedial(string deviceId, ref LinkDeviceConnection? state);
}
