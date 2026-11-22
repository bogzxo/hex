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
    private sealed record LinkDeviceConnection(bool Connecting, DateTime? RetryAtUtc, string? Reason, CancellationTokenSource? Attempt = null);
    private readonly Dictionary<string, LinkDeviceConnection> linkManualDials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (HexLinkPeer Peer, long Sampled, double? Milliseconds)> linkLatencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Badge State, Label Details, HexLinkKnown Device, bool HasAddress)> linkOfflineRows = [];

    private Panel LinkDeviceCard(HexLinkKnown known, IReadOnlyList<LinkConflictShown> conflicts)
    {
        string id = known.Device.Id;
        HexLinkPeer? peer = LinkPeerOf(id);
        bool hasAddress = peer is not null || LinkReconnectAddresses(known).Length > 0;
        Vector4 tint = LinkDeviceTint(id, Skin);
        Panel card = StripedCard(tint);
        var column = card.Add(new StackPanel { Spacing = 5, Fill = UIFill.Horizontal });
        var top = LinkActionRow(column, 24);
        top.Add(new Label
        {
            Text = $"[color:{HexColour(tint)}]$(vm)[/color] [b]{UIRichText.Literal(OneLine(known.Shown, 18))}[/b]",
            TextScale = LINK_HEADING, Size = new Vector2(140, 24), Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = known.Shown
        });
        UIComponent stateSlot = peer is null
            ? top.Add(new Panel { Size = new Vector2(117, 24), Anchor = Origin.Left, Pivot = Origin.Left })
            : top;
        var badge = stateSlot.Add(new Badge { TextScale = 0.115f, Anchor = Origin.Left, Pivot = Origin.Left });
        if (peer is null)
        {
            Label details = column.Add(LinkLine(string.Empty));
            linkOfflineRows.Add((badge, details, known, hasAddress));
            DrawLinkOfflineRow(badge, details, known, hasAddress);
        }
        else
        {
            badge.Text = Said("link.device_connected");
            badge.Tone = UIBadgeTone.Success;
            badge.Dot = true;
            if (LinkTlsOf(id) is { } tls)
                badge.Tooltip = LinkTlsSaid(tls);
        }

        if (known.Named is not null)
            column.Add(LinkLine(Said("link.device_calls_itself", ("name", known.Device.Name))));
        AddLinkNetwork(top, known, peer);
        string platform = LinkPlatform(peer?.Device ?? known.Device);

        if (peer is not null && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
        {
            HexLinkWorkspace workspace = peer.Workspace;
            string name = workspace.Name.Length > 0 ? workspace.Name : string.Join(", ", workspace.Folders.Select(folder => folder.Name));
            int running = peer.Fleet.Count(row => row.State == "running" || row.State.StartsWith("running (", StringComparison.Ordinal));
            top.Add(new Label
            {
                Text = UIRichText.Literal(Said(running == 0 ? "link.devices_no_agents" : "link.devices_agent_count", ("count", running.ToString(CultureInfo.InvariantCulture)))),
                TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left
            });
            string shownWorkspace = name.Length > 0 ? OneLine(name, 90) : Said("link.devices_workspace_empty");
            column.Add(LinkLine(platform.Length > 0 ? $"{platform} · {shownWorkspace}" : shownWorkspace));
            foreach (HexLinkRepository repository in workspace.Repositories)
                column.Add(LinkWorkspaceCommit(repository));
            if (workspace.Folders.Count > 0 && workspace.Repositories.Count == 0)
                column.Add(LinkLine(Said("link.devices_commit_unavailable")));
        }

        else if (platform.Length > 0)
            column.Add(LinkLine(platform));

        DrawLinkClaimCount(column, id);
        if (peer is not null)
            LinkDeviceSyncRows(column, peer);

        StackPanel here = LinkRightChipsRow(column, Said("link.devices_rights_here"), known.Rights);
        here.Add(LinkButton(Said("link.devices_change_rights"), "edit", () => LinkChangeDeviceRights(known)));
        if (HexLinkAllowed.Has(known.Rights, HexLinkRights.Steer))
            column.Add(LinkLine(LinkPreApprovalLabel(known.RemotePreApproval)));
        if (peer is not null)
            LinkRightChipsRow(column, Said("link.devices_rights_there"), peer.Granted);
        else
        {
            var there = LinkActionRow(column, 23);
            there.Add(new Label { Text = Said("link.devices_rights_there"), TextScale = LINK_SMALL, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left });
            there.Add(new Label { Text = Said("link.device_there_unknown"), TextScale = LINK_SMALL, Dim = true, Anchor = Origin.Left, Pivot = Origin.Left });
        }

        LinkConflictShown[] forDevice = [.. conflicts.Where(conflict => conflict.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase))];
        if (forDevice.Length > 0)
            LinkActionRow(column).Add(LinkButton(Said("link.devices_resolve", ("count", forDevice.Length.ToString(CultureInfo.InvariantCulture))),
                "warning", () => LinkChooseConflict(forDevice)));

        DrawLinkDeviceActions(column, known, peer, hasAddress);
        return card;
    }

    private void AddLinkNetwork(StackPanel row, HexLinkKnown known, HexLinkPeer? peer)
    {
        if (peer is null && GetLinkDeviceConnection(known.Device.Id)?.Connecting != true)
            return;
        string address = peer?.Session.Connection.Address ?? known.Address;
        if (address.Length > 0)
        {
            bool knownIp = IPAddress.TryParse(address, out IPAddress? ip);
            bool vpn = ip is not null && LinkIsVpn(ip);
            string route = Said(!knownIp ? "link.devices_route_network" : vpn ? "link.devices_route_vpn" : "link.devices_route_lan");
            string tip = vpn && peer is not null ? Said("link.devices_route_vpn_active", ("address", address)) : address;
            row.Add(new Badge { Text = route, Tooltip = tip, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
        }
        if (peer is not null && linkLatencies.TryGetValue(known.Device.Id, out var sampled) && sampled.Peer == peer && sampled.Milliseconds is { } ms)
            row.Add(new Badge
            {
                Text = Said("link.devices_latency", ("ms", Math.Max(1, Math.Round(ms)).ToString(CultureInfo.InvariantCulture))),
                Tone = ms > 150 ? UIBadgeTone.Warning : UIBadgeTone.Neutral,
                TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left
            });
    }

    private (string Host, int Port)[] LinkReconnectAddresses(HexLinkKnown known) =>
        [.. LinkAddressesOf(known, LinkNearby, linkRedial.Addresses(known.Device.Id))
            .OrderBy(route => IPAddress.TryParse(route.Host, out IPAddress? address) && LinkIsVpn(address))];

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
        HexLinkPreset preset = (grants & ~HexLinkRights.Predict) switch
        {
            HexLinkRights.View => HexLinkPreset.Viewer,
            HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit => HexLinkPreset.Collaborator,
            _ => HexLinkPreset.Custom
        };
        LinkGrantControls(dialog.Body, preset, grants, (_, changed) => grants = changed);
        Button picker = LinkActionRow(dialog.Body).Add(LinkButton(LinkPreApprovalLabel(preApproval), "shield", () => { }));
        picker.Tooltip = Said("link.devices_preapproval_help");
        picker.OnPressed = () =>
        {
            var menu = new Menu("remote starters");
            foreach (HexLinkPreApproval policy in Enum.GetValues<HexLinkPreApproval>())
                menu.Add(LinkPreApprovalName(policy), () =>
                {
                    preApproval = policy;
                    Button measured = LinkButton(LinkPreApprovalLabel(preApproval), "shield", () => { });
                    picker.Label = measured.Label;
                    picker.Size = measured.Size;
                }).IsChecked = () => preApproval == policy;
            ContextMenu.Show(chrome, picker.Bounds.PointAt(Origin.TopLeft), menu);
        };
    }

    private static string LinkPreApprovalName(HexLinkPreApproval policy) =>
        Said("link.devices_preapproval_" + HexLinkSpawnPolicy.Word(policy));

    private static string LinkPreApprovalLabel(HexLinkPreApproval policy) =>
        Said("link.devices_preapproval", ("policy", LinkPreApprovalName(policy)));

    private StackPanel LinkRightChipsRow(StackPanel column, string caption, HexLinkRights rights)
    {
        StackPanel row = LinkActionRow(column);
        row.Add(new Label { Text = caption, TextScale = LINK_SMALL, Bold = true, Anchor = Origin.Left, Pivot = Origin.Left });
        foreach (Badge chip in LinkRightsChips(rights))
            row.Add(chip);
        return row;
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

    private Label LinkWorkspaceCommit(HexLinkRepository repo)
    {
        HexLinkRepository? local = linkServices?.Workspace.Repositories.FirstOrDefault(each => each.Name.Equals(repo.Name, StringComparison.OrdinalIgnoreCase));
        string comparison = repo.Head.Length == 0 || local is not { Head.Length: > 0 } ? Said("link.devices_commit_unknown")
            : Said(local.Head.Equals(repo.Head, StringComparison.OrdinalIgnoreCase) ? "link.devices_commit_same" : "link.devices_commit_different");
        string head = repo.Head[..Math.Min(repo.Head.Length, 8)];
        string branch = repo.Branch.Length > 0 ? OneLine(repo.Branch, 24) : Said("link.devices_commit_detached");
        return LinkLine(Said("link.devices_commit", ("repo", OneLine(repo.Name, 45)), ("branch", branch), ("head", head), ("state", comparison)));
    }

    private void DrawLinkDeviceActions(StackPanel column, HexLinkKnown known, HexLinkPeer? peer, bool hasAddress)
    {
        string id = known.Device.Id;
        LinkWorkspaceOffer? pendingOffer = linkWorkspaceOffers.GetValueOrDefault(id);
        bool canGetOffer = CanGetLinkOffer(peer);
        if (pendingOffer is not null && (peer is null || canGetOffer))
        {
            column.Add(LinkLine(LinkWorkspaceOfferTitle(pendingOffer)));
            if (peer is null)
                column.Add(LinkLine(Said("link.devices_workspace_offer_reconnect")));
        }

        var actions = LinkActionRow(column);
        Action? share = null, get = null;
        bool sharing = LinkSyncs.Any(sync => sync.DeviceId == id && sync.State != HexLinkSyncState.Off);
        bool offerShare = !(pendingOffer is not null && canGetOffer) && linkServices?.Workspace.Folders.Count > 0;
        if (peer is not null)
        {
            LinkDeviceProvisionActions(id, ref share, ref get);
            if (pendingOffer is not null && canGetOffer)
                get = () => GetLinkOfferedWorkspace(pendingOffer);
            AddLinkAction(actions, offerShare ? "link.devices_share" : "link.devices_get", offerShare ? "cloud-upload" : "cloud-download",
                offerShare ? share : get, "link.devices_provision_pending");
            AddLinkAction(actions, "link.devices_start_agent", "robot", HexLinkAllowed.Has(peer.Granted, HexLinkRights.Steer)
                ? () => LinkStartAgentOnDevice(id) : null, "link.devices_needs_steer");
            if (linkDeviceRowsNarrow)
                actions = LinkActionRow(column);
            AddLinkAction(actions, "link.devices_open_files", "folder-opened", HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)
                ? () => LinkOpenRemoteCommand(peer) : null, "link.devices_needs_view");
        }
        else
        {
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
                        LinkOn ? "link.devices_connecting" : "link.pair_off_text");
            }
        }

        Button more = actions.Add(LinkButton(Said("link.devices_more"), string.Empty, () => { }));
        more.Tooltip = Said("link.devices_more_other", ("device", known.Shown));
        if (peer is not null && (offerShare ? share : get) is null)
            column.Add(LinkLine(Said("link.devices_provision_short")));

        more.OnPressed = () =>
        {
            var menu = new Menu("device actions");
            if (peer is not null)
            {
                menu.Add(Said(offerShare ? "link.devices_get" : "link.devices_share"),
                    () => (offerShare ? get : share)?.Invoke(), icon: offerShare ? "cloud-download" : "cloud-upload").IsEnabled = () => (offerShare ? get : share) is not null;
                menu.Add(Said("link.devices_stop_sharing"), () => LinkStopDeviceSharing(known), icon: "debug-stop").IsEnabled = () => sharing;
                menu.Add(Said("link.device_disconnect"), () => LinkDisconnect(id), icon: "debug-disconnect");
            }
            menu.Add(Said("link.device_rename"), () => AskText(Said("link.rename_title"), Said("link.rename_text", ("device", known.Device.Name)),
                known.Shown, Said("link.rename_button"), typed => LinkRename(id, typed)), icon: "edit");
            if (known.Named is not null)
                menu.Add(Said("link.device_own_name"), () => LinkUseOwnName(id), icon: "discard");
            menu.Add(Said("link.device_forget"), () => AskForgetDevice(known), icon: "trash");
            ContextMenu.Show(chrome, more.Bounds.PointAt(Origin.TopLeft), menu);
        };
    }

    private void AddLinkAction(StackPanel row, string labelKey, string icon, Action? action, string unavailable)
    {
        Button button = row.Add(LinkButton(Said(labelKey), icon, () => action?.Invoke()));
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

    private void LinkChooseConflict(IReadOnlyList<LinkConflictShown> conflicts)
    {
        ChooseFrom(Said("link.devices_conflict_title"), Said("link.devices_conflict_help"), [.. conflicts.Select(conflict => (conflict.Path, (Action)(() =>
        {
            ChooseFrom(Said("link.devices_conflict_title"), conflict.Path,
            [
                (Said("link.conflict_difference"), () => LinkOpenConflictDifference(conflict.DeviceId, conflict.Path)),
                (Said("link.conflict_mine"), () => LinkResolveGo(conflict, HexLinkSyncKeep.Mine)),
                (Said("link.conflict_theirs"), () => LinkResolveGo(conflict, HexLinkSyncKeep.Theirs))
            ]);
        })))]);
    }

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
            { Connecting: true } => "link.devices_connecting",
            { RetryAtUtc: not null } => "link.devices_retrying",
            _ when reason is "version" => "link.devices_version",
            _ when reason is "refused" or "pin" or "unknown" => "link.devices_refused",
            _ when reason is "unreachable" or "timeout" or "firewall" => "link.devices_unreachable",
            _ when LinkHeldOff(id) => "link.device_held_off",
            _ => "link.device_not_connected"
        };
        string seconds = state?.RetryAtUtc is { } retry ? Math.Max(0, (int)Math.Ceiling((retry - DateTime.UtcNow).TotalSeconds)).ToString(CultureInfo.InvariantCulture) : "0";
        string text = Said(key, ("seconds", seconds));
        if (badge.Text != text)
            badge.Text = text;
        badge.Tone = state?.Connecting == true ? UIBadgeTone.Info : key == "link.devices_refused" ? UIBadgeTone.Danger
            : reason is not null ? UIBadgeTone.Warning : UIBadgeTone.Neutral;
        badge.Colour = badge.Tone == UIBadgeTone.Info ? LinkBadgeInfoColour : null;
        string action = reason is null
            ? Said(!LinkOn ? "link.pair_off_text" : LinkHeldOff(id) ? "link.devices_held_help" : "link.devices_offline_help")
            : LinkDialFailureSaid(reason);
        if (state?.Connecting == true)
            action = Said("link.devices_trying_route", ("address", known.Address));
        else if (state?.RetryAtUtc is not null)
            action = hasAddress ? Said("link.devices_retry_help", ("why", action)) : Said("link.devices_retry_no_address");
        else if (key == "link.device_not_connected" && LinkOn)
            action = Said("link.devices_last_seen", ("time", known.Seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), ("next", action));
        string escaped = UIRichText.Literal(action);
        if (details.Text != escaped)
            details.Text = escaped;
        details.Visible = true;
    }

    private static string LinkDialFailureSaid(string reason) => reason switch
    {
        "version" => Said("link.devices_version_help"),
        "refused" or "pin" or "unknown" => Said("link.devices_refused_help"),
        "unreachable" or "timeout" or "firewall" => Said("link.devices_firewall_help"),
        _ => Said("link.devices_dial_help", ("why", OneLine(reason, 150).TrimEnd('.')))
    };

    private void TickLinkDeviceRows()
    {
        foreach (var (badge, details, device, hasAddress) in linkOfflineRows)
            DrawLinkOfflineRow(badge, details, device, hasAddress);
        long now = System.Environment.TickCount64;
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
