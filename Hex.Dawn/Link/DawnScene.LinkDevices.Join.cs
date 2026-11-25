using System.Globalization;
using System.Net;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private enum LinkDevicePage { Overview, Invite, Join }

    private sealed record LinkInviteOptions(HexLinkPreset Preset, HexLinkRights Rights, bool Share);
    private LinkInviteOptions linkInviteOptions
    {
        get => LazyInitializer.EnsureInitialized(ref field, () => new(HexLinkPreset.Collaborator, LinkInviteDefaultRights(), true));
        set => Volatile.Write(ref field, value);
    }
    private HexLinkPreset linkInvitePreset => linkInviteOptions.Preset;
    private HexLinkRights linkInviteRights => linkInviteOptions.Rights;

    private LinkDevicePage linkDevicePage;
    private UIDialog? linkOnboardingDialog;
    private Button? linkJoinSubmit, linkCopyInvite;
    private ToggleButton? linkInviteShare;
    private Label? linkJoinStatus;
    private Panel? linkJoinField;
    private TextBox? linkJoinRouteBox, linkInviteRouteBox;
    private bool linkJoining, linkJoinError, linkReopenJoin, linkInviteExpired, linkInviteRouteEditing;
    private string? linkJoinMessage, linkInviteError;
    private string linkJoinTyped = string.Empty, linkJoinRouteTyped = string.Empty, linkInviteRouteTyped = string.Empty;
    private long linkInviteCopiedUntil;
    private bool linkAutoShareAvailable, linkInviteAddressesShown;
    private string? linkShareAfterPairing;
    private (string Id, long Until)? linkShareAfterConnected;

    private void TickLinkAutoShare()
    {
        if (linkShareAfterConnected is not { } pending)
            return;
        if (System.Environment.TickCount64 >= pending.Until)
        {
            linkShareAfterConnected = null;
            return;
        }

        if (LinkPeerOf(pending.Id) is null)
            return;
        linkShareAfterConnected = null;
        // The share that was asked for with the invitation goes as it is: the question about build folders is asked when a
        // sync of a folder is started from the window, not over the window the person has just paired in
        SendLinkWorkspaceOffer(pending.Id);
    }

    private void ShowLinkDevicePage(LinkDevicePage page)
    {
        ShowLinkWindow();
        if (linkOnboardingDialog is { IsOpen: true } && linkDevicePage == page)
            return;

        string title = Said(page == LinkDevicePage.Invite ? "link.devices_invite" : "link.devices_join");
        UIDialog dialog = page == LinkDevicePage.Invite
            ? UIDialog.Show(chrome, title, string.Empty, new DialogChoice(Said("link.devices_done")),
                new DialogChoice(Said("link.devices_cancel_invite"), LinkClosePairing))
            : UIDialog.Show(chrome, title, string.Empty, new DialogChoice(Said("link.pair_cancel")));
        linkOnboardingDialog = dialog;
        linkDevicePage = page;
        linkGrantChoicesOpen = linkInviteAddressesShown = false;
        dialog.Body.Size = new Vector2(Math.Clamp(chrome.Root.Bounds.Width - 80, 350, 460), 0);
        dialog.Body.Spacing = 7;
        dialog.Closed = () =>
        {
            if (linkOnboardingDialog != dialog)
                return;
            linkOnboardingDialog = null;
            linkWindowNearby = null;
            linkAddressBox = linkJoinRouteBox = linkInviteRouteBox = null;
            linkJoinField = null;
            linkInviteRouteEditing = false;
            linkInviteRouteTyped = string.Empty;
            linkJoinSubmit = linkCopyInvite = null;
            linkJoinStatus = linkPairingCountdown = null;
            linkDevicePage = LinkDevicePage.Overview;
            if (!linkJoining)
                linkJoinTyped = linkJoinRouteTyped = string.Empty;
        };

        if (page == LinkDevicePage.Invite)
        {
            linkInviteExpired = false;
            LinkCanAutoShare(ref linkAutoShareAvailable);
            if (LinkOn && LinkPort != 0 && LinkPairingUntil is null)
            {
                linkInviteOptions = new(HexLinkPreset.Collaborator, LinkInviteDefaultRights(), true);
                LinkCreateInvite();
            }
        }
        else
        {
            BuildLinkJoin(dialog.Body);
            linkAddressBox?.Focus();
        }
        DrawLinkPairing();
        DrawLinkNearby();
    }

    private void DrawLinkPairing()
    {
        if (linkOnboardingDialog is not { IsOpen: true } dialog)
            return;
        if (linkDevicePage == LinkDevicePage.Invite)
        {
            if (linkPairingCountdown is not null && LinkPairingUntil is null)
                linkInviteExpired = true;
            DrawInvite(dialog.Body);
        }
        else
            DrawLinkJoinStatus();
    }

    private void LinkCreateInvite() => LinkCreateInvite(null);

    private void LinkCreateInvite(HexLinkInviteRoute? route)
    {
        if (LinkOpenInvite(route))
        {
            linkInviteError = null;
            linkInviteRouteEditing = false;
        }
        else
            linkInviteError = Said(route is null ? "link.devices_no_route" : "link.devices_public_invite_failed");
        linkInviteExpired = false;
        linkInviteCopiedUntil = 0;
        linkWindowStale = true;
    }

    private void LinkChoosePublicInvite()
    {
        if (!settings.LinkAllowPublic || LinkPort == 0)
            return;
        linkInviteRouteTyped = LinkActiveInvite?.PublicRoute?.ToString() ?? LinkMappedEndPoint?.ToString() ?? string.Empty;
        linkInviteRouteEditing = true;
        DrawLinkPairing();
        linkInviteRouteBox?.Focus();
    }

    private void DrawInviteRoute(StackPanel body, HexLinkInvite? token)
    {
        if (token?.PublicRoute is { } route)
        {
            body.Add(LinkLine(Said("link.devices_public_invite_route", ("address", route.ToString()))));
            LinkActionRow(body).Add(LinkButton(Said("link.devices_private_invite"), "add", LinkCreateInvite));
        }

        if (!settings.LinkAllowPublic)
            return;
        if (!linkInviteRouteEditing)
        {
            LinkActionRow(body).Add(LinkButton(Said("link.devices_public_invite"), "plug", LinkChoosePublicInvite));
            return;
        }

        string prompt = Said("link.devices_public_invite_prompt");
        body.Add(LinkLine(prompt));
        TextBox field = linkInviteRouteBox = body.Add(new TextBox
        {
            Text = linkInviteRouteTyped, Placeholder = Said("link.devices_public_invite_host"),
            MaxLength = HexLinkInviteRoute.MaxHostLength + 8, Size = new Vector2(Math.Min(body.Size.X, 360), LINK_ROW),
            Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = prompt
        });
        field.OnChanged = _ => linkInviteRouteTyped = field.Text;
        field.OnSubmitted = _ => LinkConfirmPublicInvite();
        StackPanel actions = LinkActionRow(body);
        actions.Add(LinkButton(Said("link.devices_public_invite_create"), "plug", LinkConfirmPublicInvite)).Primary = true;
        actions.Add(LinkButton(Said("link.pair_cancel"), string.Empty, () =>
        {
            linkInviteRouteEditing = false;
            linkInviteError = null;
            DrawLinkPairing();
        }));
    }

    private void LinkConfirmPublicInvite()
    {
        string address = linkInviteRouteBox?.Text ?? linkInviteRouteTyped;
        if (TryInviteRoute(address, LinkMappedPort ?? LinkPort, out var route))
            LinkCreateInvite(route);
        else
            linkInviteError = Said("link.devices_invite_bad_route");

        DrawLinkPairing();
        if (linkInviteRouteEditing)
            linkInviteRouteBox?.Focus();
    }

    private void DrawInvite(StackPanel body)
    {
        ClearRows(body);
        linkPairingCountdown = null;
        linkCopyInvite = null;
        linkInviteShare = null;
        linkInviteRouteBox = null;
        linkCountdownShown = -1;
        if (!LinkOn || LinkPort == 0)
        {
            body.Add(LinkLine(Said(LinkOn ? "link.pair_not_here" : "link.pair_off_text")));
            if (!LinkOn)
                LinkActionRow(body).Add(LinkButton(Said("link.devices_turn_on"), "plug", () => LinkSwitch(true)));
            return;
        }

        if (LinkPairingUntil is not null)
        {
            HexLinkInvite? token = LinkActiveInvite;
            if (token is null)
            {
                // The older window, opened by the Pair command: the addresses to type on the other machine
                body.Add(LinkLine(Said("link.pair_addresses")));
                DrawInviteAddresses(body, null);
                StackPanel stop = LinkActionRow(body);
                linkPairingCountdown = stop.Add(LinkCountdown());
                stop.Add(LinkButton(Said("link.pair_stop"), "debug-stop", () =>
                {
                    LinkClosePairing();
                    linkWindowStale = true;
                }));
                return;
            }

            // One: what the other device may do here, decided before it arrives
            body.Add(LinkHeading(Said("link.devices_invite_step_grant")));
            LinkGrantControls(body, linkInvitePreset, linkInviteRights, (preset, rights) =>
            {
                linkInviteOptions = linkInviteOptions with
                {
                    Preset = preset, Rights = rights, Share = preset == HexLinkPreset.Worker || linkInviteOptions.Share
                };
                if (linkInviteShare is not null)
                    linkInviteShare.State = linkAutoShareAvailable && linkInviteOptions.Share;
                // The last choice of the agents tick under Work together is what the next invite starts from
                if (preset == HexLinkPreset.Collaborator)
                {
                    string kept = (rights & HexLinkRights.Steer) != 0 ? "use_agents" : "work_together";
                    if (settings.LinkInviteRights != kept)
                    {
                        settings.LinkInviteRights = kept;
                        SaveSettings();
                    }
                }
            }, showBoth: true, collapse: true, agentsTick: true);
            ToggleButton share = linkInviteShare = body.Add(new ToggleButton(Said("link.devices_auto_share"), linkAutoShareAvailable && linkInviteOptions.Share)
            {
                Enabled = linkAutoShareAvailable, Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT,
                Tooltip = Said(linkAutoShareAvailable ? "link.devices_auto_share_tip" : "link.devices_provision_pending")
            });
            share.OnPressed = () => linkInviteOptions = linkInviteOptions with { Share = share.State };
            if (!linkAutoShareAvailable)
                body.Add(LinkLine(Said("link.devices_auto_share_unavailable")));

            // Two: the two ways across, both open at once. Picked under Nearby it pairs by the six digits, with the
            // invite it pairs by the invite, and whichever comes first is the invitation's one device
            body.Add(LinkHeading(Said("link.devices_invite_step_connect")));
            string name = HexLinkNames.Clean(LinkDevice?.Name) ?? string.Empty;
            body.Add(LinkLine(Said("link.devices_invite_way_nearby", ("name", name))));
            body.Add(LinkLine(Said("link.devices_invite_way_token")));
            DrawInviteToken(body, token);
            // The raw addresses are for somebody typing one in, behind a button for them
            StackPanel addresses = body.Add(new StackPanel { Spacing = 4, Fill = UIFill.Horizontal, Visible = linkInviteAddressesShown });
            DrawInviteAddresses(addresses, token);
            Button show = LinkActionRow(body).Add(LinkButton(Said(linkInviteAddressesShown ? "link.devices_hide_addresses" : "link.devices_show_addresses"), string.Empty, () => { }));
            show.OnPressed = () =>
            {
                linkInviteAddressesShown = !linkInviteAddressesShown;
                addresses.Visible = linkInviteAddressesShown;
                show.Label = Said(linkInviteAddressesShown ? "link.devices_hide_addresses" : "link.devices_show_addresses");
            };
            DrawInviteRoute(body, token);
            if (linkInviteError is not null)
                body.Add(LinkLine(linkInviteError));

            StackPanel waiting = LinkActionRow(body);
            waiting.Add(new Label
            {
                Text = $"$(plug) {UIRichText.Literal(Said("link.devices_invite_waiting"))}", TextScale = LINK_TEXT, Dim = true,
                Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left
            });
            linkPairingCountdown = waiting.Add(LinkCountdown());
        }
        else
        {
            body.Add(LinkLine(linkInviteError ?? Said(linkInviteExpired ? "link.devices_invite_expired" : "link.devices_invite_intro")));
            StackPanel row = LinkActionRow(body);
            row.Add(LinkButton(Said("link.devices_create_invite"), "add", LinkCreateInvite)).Primary = true;
            DrawInviteRoute(body, null);
        }
    }

    // Where this machine can be reached, for somebody typing an address on the other
    private void DrawInviteAddresses(StackPanel body, HexLinkInvite? token)
    {
        IReadOnlyList<IPAddress> addresses = token?.Addresses ?? InviteAddresses();
        if (addresses.Count == 0 && token?.PublicRoute is null)
            body.Add(LinkLine(Said("link.pair_addresses_none", ("port", LinkPort.ToString(CultureInfo.InvariantCulture)))));
        else if (addresses.Count > 0)
            body.Add(LinkLine(string.Join("   ·   ", addresses.Select(address =>
                Said(LinkIsVpn(address) ? "link.devices_endpoint_vpn" : "link.devices_endpoint_lan",
                    ("address", new IPEndPoint(address, LinkPort).ToString()))))));
    }

    // How long the invitation has left, written by the window's tick
    private static Label LinkCountdown() =>
        new() { TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Size = new Vector2(110, LINK_ROW) };


    private void LinkStartAddressPairing()
    {
        LinkPairCommand();
        linkWindowStale = true;
    }

    private void DrawInviteToken(StackPanel body, HexLinkInvite token)
    {
        string encoded = token.Encode();
        var row = LinkActionRow(body);
        row.Add(new TextBox
        {
            Text = encoded, ReadOnly = true, Size = new Vector2(Math.Clamp(body.Size.X - 110, 230, 330), LINK_ROW),
            Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("link.devices_invite_help")
        });
        Button copy = row.Add(LinkButton(Said("link.devices_copy_invite"), "copy", () =>
        {
            if (LinkActiveInvite != token)
                return;
            (linkInviteCopyForTest ?? UIClipboard.Copy)(encoded);
            linkInviteCopiedUntil = System.Environment.TickCount64 + 2000;
            TickLinkInviteCopy();
        }));
        copy.Tooltip = Said("link.devices_invite_help");
        copy.Primary = true;
        copy.Size = new Vector2(104, LINK_ROW);
        linkCopyInvite = copy;
        TickLinkInviteCopy();
    }

    private void TickLinkInviteCopy()
    {
        if (linkCopyInvite is null)
            return;
        // The words are looked up when the state moves (copied, back to Copy), not every frame
        bool copied = System.Environment.TickCount64 < linkInviteCopiedUntil;
        if (linkInviteCopyShown == copied && ReferenceEquals(linkInviteCopyFor, linkCopyInvite))
            return;
        linkInviteCopyShown = copied;
        linkInviteCopyFor = linkCopyInvite;
        string label = Said(copied ? "link.devices_copied" : "link.devices_copy_invite");
        if (linkCopyInvite.Label != label)
            linkCopyInvite.Label = label;
    }

    private bool? linkInviteCopyShown;
    private Button? linkInviteCopyFor;

    private void BuildLinkJoin(StackPanel body)
    {
        // One: a device on this network is picked from the list. Two: anything else is pasted or typed
        linkWindowNearby = body.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 5 });
        body.Add(LinkHeading(Said("link.devices_join_step_typed")));
        StackPanel row = LinkActionRow(body);
        linkJoinField = row.Add(new Panel
        {
            Size = new Vector2(Math.Clamp(body.Size.X - 92, 220, 340), LINK_ROW), Padding = new UIEdges(1),
            Anchor = Origin.Left, Pivot = Origin.Left, Radius = 4
        });
        linkAddressBox = linkJoinField.Add(new TextBox
        {
            Text = linkJoinTyped, Placeholder = Said("link.devices_join_placeholder"), MaxLength = HexLinkInvite.MaxTokenLength,
            Fill = UIFill.Both, Tooltip = Said("link.devices_join_help")
        });
        linkAddressBox.OnSubmitted = _ => JoinTyped();
        linkAddressBox.OnChanged = _ => LinkSetJoinMessage(null);
        linkJoinSubmit = row.Add(LinkButton(Said("link.devices_join_submit"), "plug", JoinTyped));
        linkJoinSubmit.Primary = true;
        linkJoinRouteBox = body.Add(new TextBox
        {
            Text = linkJoinRouteTyped, Placeholder = Said("link.devices_join_route"), MaxLength = HexLinkInviteRoute.MaxHostLength + 8,
            Size = linkJoinField.Size, Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("link.devices_join_route_tip"),
            Visible = StartsWithInvite(linkJoinTyped)
        });
        linkJoinRouteBox.OnSubmitted = _ => JoinTyped();
        linkJoinRouteBox.OnChanged = _ => LinkSetJoinMessage(null);
        linkJoinStatus = body.Add(LinkLine(string.Empty));
    }

    private void DrawLinkJoinStatus()
    {
        if (linkAddressBox is null || linkJoinSubmit is null || linkJoinStatus is null)
            return;
        linkAddressBox.Enabled = LinkOn && !linkJoining;
        if (linkJoinRouteBox is not null)
        {
            linkJoinRouteBox.Visible = StartsWithInvite(linkAddressBox.Text.TrimStart());
            linkJoinRouteBox.Enabled = LinkOn && !linkJoining;
        }
        linkJoinSubmit.Enabled = LinkOn && !linkJoining && !linkJoinError && linkAddressBox.Text.Trim().Length > 0;
        string said = UIRichText.Literal(linkJoinMessage ?? Said(LinkOn ? "link.devices_join_help" : "link.pair_off_text"));
        bool error = linkJoinError && linkJoinMessage is not null;
        Vector4 tint = error ? Skin?.Palette.Error ?? LinkWarning : Vector4.Zero;
        if (linkJoinField is not null)
            linkJoinField.Outline = tint;
        linkJoinStatus.Text = error ? $"[color:{HexColour(tint)}]$(error) {said}[/color]" : said;
        linkJoinStatus.Dim = linkJoinMessage is null;
    }

    private static bool StartsWithInvite(string text) => text.StartsWith("hex-link:", StringComparison.OrdinalIgnoreCase);

    private static bool TryInviteRoute(string text, int defaultPort, out HexLinkInviteRoute? route)
    {
        route = null;
        return TryLinkAddress(text, defaultPort, out string host, out int port) &&
            HexLinkInviteRoute.TryCreate(host, port, out route);
    }

    private void JoinTyped()
    {
        if (!LinkOn || linkJoining || linkJoinError || linkAddressBox is null)
            return;
        string text = linkAddressBox.Text.Trim();
        if (StartsWithInvite(text))
            text = HexLinkInvite.Clean(text);
        linkJoinTyped = text;
        if (StartsWithInvite(text))
        {
            string? problem = HexLinkInvite.Problem(text);
            if (problem is not null || !HexLinkInvite.TryParse(text, out HexLinkInvite? parsed) || parsed is null)
            {
                LinkSetJoinMessage(Said(problem switch
                {
                    "expired" => "link.devices_invite_expired_join",
                    "version" => "link.devices_invite_version",
                    "cut_off" => "link.devices_invite_cut_off",
                    _ => "link.devices_bad_invite"
                }), error: true);
                return;
            }

            linkJoinRouteTyped = linkJoinRouteBox?.Text.Trim() ?? string.Empty;
            HexLinkInviteRoute? route = null;
            if (linkJoinRouteTyped.Length > 0 && !TryInviteRoute(linkJoinRouteTyped, parsed.PublicRoute?.Port ?? parsed.Port, out route))
            {
                LinkSetJoinMessage(Said("link.devices_invite_bad_route"), error: true);
                return;
            }
            JoinDevice("", parsed.Port, parsed, route);
        }
        else if (TryLinkAddress(text, HexLinkTransport.DEFAULT_PORT, out string host, out int port))
            JoinDevice(host, port);
        else
            LinkSetJoinMessage(Said("link.devices_bad_address"), error: true);
    }

    private void JoinDevice(string host, int port, HexLinkInvite? invite = null, HexLinkInviteRoute? overrideRoute = null)
    {
        if (!LinkOn || linkJoining)
            return;
        linkJoining = true;
        LinkSetJoinMessage(Said("link.devices_connecting"));
        string? inviteEndpoint = (overrideRoute ?? (settings.LinkAllowPublic ? invite?.PublicRoute : null))?.ToString();
        HexLinkKnown? known = invite is null ? LinkKnown.FirstOrDefault(each => each.Address == host && (each.Port == port || each.Port == 0)) : null;
        _ = Task.Run(async () =>
        {
            string? outcome;
            try
            {
                if (invite is null && known is not null)
                {
                    try
                    {
                        await LinkConnect(host, port, known.Device.Id);
                        PostLink(() => LinkFinishJoin(null));
                        return;
                    }
                    catch (HexLinkRefused e) when (e.Reason is "unknown" or "pin")
                    {
                        // The address needs a fresh pair. The comparison code still needs approval on both devices.
                    }
                }

                HexLinkPairResult result = await LinkPair(host, port, invite: invite, overrideRoute: overrideRoute);
                outcome = result.Paired ? null : LinkJoinFailure(result.Reason, host, inviteEndpoint);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
            {
                outcome = LinkJoinFailure(e is HexLinkRefused refused ? refused.Reason : "failed", host, inviteEndpoint);
            }

            PostLink(() => LinkFinishJoin(outcome));
        });
    }

    private static string LinkJoinFailure(string reason, string host, string? inviteEndpoint) => reason switch
    {
        "invite_expired" => Said("link.devices_invite_expired_join"),
        "unreachable" or "timeout" when inviteEndpoint is not null =>
            Said("link.devices_invite_endpoint_unreachable", ("address", inviteEndpoint)),
        "unreachable" or "timeout" => Said("link.devices_join_unreachable", ("address", host.Length > 0 ? host : Said("link.devices_invite_address"))),
        "version" => Said("link.devices_version_help"),
        "declined-there" => Said("link.devices_join_declined"),
        _ => Said("link.devices_join_failed", ("why", LinkPairReasonSaid(reason)))
    };

    private void LinkFinishJoin(string? outcome)
    {
        linkJoining = false;
        bool reopen = linkReopenJoin;
        linkReopenJoin = false;
        LinkSetJoinMessage(outcome, error: outcome is not null);
        if (outcome is null)
        {
            linkJoinTyped = linkJoinRouteTyped = string.Empty;
            if (linkDevicePage == LinkDevicePage.Join)
                linkOnboardingDialog?.Close();
        }
        else if (reopen && linkWindow is not null)
            ShowLinkDevicePage(LinkDevicePage.Join);
    }

    private void LinkSetJoinMessage(string? message, bool error = false)
    {
        linkJoinMessage = message;
        linkJoinError = error;
        DrawLinkJoinStatus();
    }

    private IReadOnlyList<IPAddress> InviteAddresses()
    {
        if (linkTransport?.ListeningAddress is { } address && IPAddress.IsLoopback(address))
            return [address];
        List<IPAddress> routes = HexLinkAddresses.Preferred(LinkLocalAddresses());
        if (routes.Count > HexLinkInvite.MaxEndpoints && routes.FirstOrDefault(LinkIsVpn) is { } vpn && !routes.Take(HexLinkInvite.MaxEndpoints).Contains(vpn))
            return [.. routes.Take(HexLinkInvite.MaxEndpoints - 1), vpn];
        return [.. routes.Take(HexLinkInvite.MaxEndpoints)];
    }

    internal static bool LinkIsVpn(IPAddress address) => HexLinkInvite.IsVpn(address);

    partial void LinkCanAutoShare(ref bool available);
}
