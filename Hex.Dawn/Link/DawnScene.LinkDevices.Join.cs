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
    private LinkInviteOptions linkInviteOptions { get => field ??= new(HexLinkPreset.Collaborator, LinkPresetRights(HexLinkPreset.Collaborator), true); set; }
    private HexLinkPreset linkInvitePreset => linkInviteOptions.Preset;
    private HexLinkRights linkInviteRights => linkInviteOptions.Rights;

    private LinkDevicePage linkDevicePage;
    private UIDialog? linkOnboardingDialog;
    private Button? linkJoinSubmit, linkCopyInvite;
    private ToggleButton? linkInviteShare;
    private Label? linkJoinStatus;
    private Panel? linkJoinField;
    private bool linkJoining, linkJoinError, linkReopenJoin, linkInviteExpired;
    private string? linkJoinMessage, linkInviteError;
    private string linkJoinTyped = string.Empty;
    private long linkInviteCopiedUntil;
    private bool linkAutoShareAvailable;
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
        Action? share = null, get = null;
        LinkDeviceProvisionActions(pending.Id, ref share, ref get);
        share?.Invoke();
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
        dialog.Body.Size = new Vector2(Math.Clamp(chrome.Root.Bounds.Width - 80, 350, 460), 0);
        dialog.Body.Spacing = 7;
        dialog.Closed = () =>
        {
            if (linkOnboardingDialog != dialog)
                return;
            linkOnboardingDialog = null;
            linkWindowNearby = null;
            linkAddressBox = null;
            linkJoinField = null;
            linkJoinSubmit = linkCopyInvite = null;
            linkJoinStatus = linkPairingCountdown = null;
            linkDevicePage = LinkDevicePage.Overview;
            if (!linkJoining)
                linkJoinTyped = string.Empty;
        };

        if (page == LinkDevicePage.Invite)
        {
            linkInviteExpired = false;
            LinkCanAutoShare(ref linkAutoShareAvailable);
            if (LinkOn && LinkPort != 0 && LinkPairingUntil is null)
            {
                Volatile.Write(ref linkInviteOptions, new LinkInviteOptions(HexLinkPreset.Collaborator, LinkPresetRights(HexLinkPreset.Collaborator), true));
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

    private void LinkCreateInvite()
    {
        linkInviteError = LinkOpenInvite() ? null : Said("link.devices_no_route");
        linkInviteExpired = false;
        linkInviteCopiedUntil = 0;
        linkWindowStale = true;
    }

    private void DrawInvite(StackPanel body)
    {
        ClearRows(body);
        linkPairingCountdown = null;
        linkCopyInvite = null;
        linkInviteShare = null;
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
            body.Add(LinkLine(Said(token is null ? "link.pair_addresses" : "link.devices_invite_help")));
            if (token is not null)
                DrawInviteToken(body, token);

            IReadOnlyList<IPAddress> addresses = token?.Addresses ?? InviteAddresses();
            if (addresses.Count == 0)
                body.Add(LinkLine(Said("link.pair_addresses_none", ("port", LinkPort.ToString(CultureInfo.InvariantCulture)))));
            else
                body.Add(LinkLine(string.Join("   ·   ", addresses.Select(address =>
                    Said(LinkIsVpn(address) ? "link.devices_endpoint_vpn" : "link.devices_endpoint_lan",
                        ("address", new IPEndPoint(address, LinkPort).ToString()))))));

            StackPanel commands = LinkActionRow(body);
            linkPairingCountdown = commands.Add(new Label { TextScale = LINK_TEXT, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Size = new Vector2(110, LINK_ROW) });
            if (token is null)
            {
                commands.Add(LinkButton(Said("link.pair_stop"), "debug-stop", () =>
                {
                    LinkClosePairing();
                    linkWindowStale = true;
                }));
                return;
            }

            LinkGrantControls(body, linkInvitePreset, linkInviteRights, (preset, rights) =>
            {
                Volatile.Write(ref linkInviteOptions, linkInviteOptions with
                {
                    Preset = preset, Rights = rights, Share = preset == HexLinkPreset.Worker || linkInviteOptions.Share
                });
                if (linkInviteShare is not null)
                    linkInviteShare.State = linkAutoShareAvailable && linkInviteOptions.Share;
            }, showBoth: true);
            ToggleButton share = linkInviteShare = body.Add(new ToggleButton(Said("link.devices_auto_share"), linkAutoShareAvailable && linkInviteOptions.Share)
            {
                Enabled = linkAutoShareAvailable, Anchor = Origin.Left, Pivot = Origin.Left, LabelScale = LINK_TEXT,
                Tooltip = Said(linkAutoShareAvailable ? "link.devices_auto_share_tip" : "link.devices_provision_pending")
            });
            share.OnPressed = () => Volatile.Write(ref linkInviteOptions, linkInviteOptions with { Share = share.State });
            if (!linkAutoShareAvailable)
                body.Add(LinkLine(Said("link.devices_auto_share_unavailable")));
        }
        else
        {
            body.Add(LinkLine(linkInviteError ?? Said(linkInviteExpired ? "link.devices_invite_expired" : "link.devices_invite_intro")));
            StackPanel row = LinkActionRow(body);
            row.Add(LinkButton(Said("link.devices_create_invite"), "add", LinkCreateInvite)).Primary = true;
        }
    }

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
        string label = Said(System.Environment.TickCount64 < linkInviteCopiedUntil ? "link.devices_copied" : "link.devices_copy_invite");
        if (linkCopyInvite.Label != label)
            linkCopyInvite.Label = label;
    }

    private void BuildLinkJoin(StackPanel body)
    {
        StackPanel row = LinkActionRow(body);
        linkJoinField = row.Add(new Panel
        {
            Size = new Vector2(Math.Clamp(body.Size.X - 92, 220, 340), LINK_ROW), Padding = new UIEdges(1),
            Anchor = Origin.Left, Pivot = Origin.Left, Radius = 4
        });
        linkAddressBox = linkJoinField.Add(new TextBox
        {
            Text = linkJoinTyped, Placeholder = Said("link.devices_join_placeholder"), MaxLength = 320,
            Fill = UIFill.Both, Tooltip = Said("link.devices_join_help")
        });
        linkAddressBox.OnSubmitted = _ => JoinTyped();
        linkAddressBox.OnChanged = _ => LinkSetJoinMessage(null);
        linkJoinSubmit = row.Add(LinkButton(Said("link.devices_join_submit"), "plug", JoinTyped));
        linkJoinSubmit.Primary = true;
        linkJoinStatus = body.Add(LinkLine(string.Empty));
        linkWindowNearby = body.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 5 });
    }

    private void DrawLinkJoinStatus()
    {
        if (linkAddressBox is null || linkJoinSubmit is null || linkJoinStatus is null)
            return;
        linkAddressBox.Enabled = LinkOn && !linkJoining;
        linkJoinSubmit.Enabled = LinkOn && !linkJoining && !linkJoinError && linkAddressBox.Text.Trim().Length > 0;
        string said = UIRichText.Literal(linkJoinMessage ?? Said(LinkOn ? "link.devices_join_help" : "link.pair_off_text"));
        bool error = linkJoinError && linkJoinMessage is not null;
        Vector4 tint = error ? Skin?.Palette.Error ?? LinkWarning : Vector4.Zero;
        if (linkJoinField is not null)
            linkJoinField.Outline = tint;
        linkJoinStatus.Text = error ? $"[color:{HexColour(tint)}]$(error) {said}[/color]" : said;
        linkJoinStatus.Dim = linkJoinMessage is null;
    }

    private void JoinTyped()
    {
        if (!LinkOn || linkJoining || linkJoinError || linkAddressBox is null)
            return;
        string text = linkAddressBox.Text.Trim();
        linkJoinTyped = text;
        if (text.StartsWith("hex-link:", StringComparison.OrdinalIgnoreCase))
        {
            if (!HexLinkInvite.TryParse(text, out HexLinkInvite? parsed) || parsed is null)
            {
                LinkSetJoinMessage(Said("link.devices_bad_invite"), error: true);
                return;
            }

            JoinDevice("", parsed.Port, parsed);
        }
        else if (TryLinkAddress(text, HexLinkTransport.DEFAULT_PORT, out string host, out int port))
            JoinDevice(host, port);
        else
            LinkSetJoinMessage(Said("link.devices_bad_address"), error: true);
    }

    private void JoinDevice(string host, int port, HexLinkInvite? invite = null)
    {
        if (!LinkOn || linkJoining)
            return;
        linkJoining = true;
        LinkSetJoinMessage(Said("link.devices_connecting"));
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

                HexLinkPairResult result = await LinkPair(host, port, invite: invite);
                outcome = result.Paired ? null : LinkJoinFailure(result.Reason, host);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
            {
                outcome = LinkJoinFailure(e is HexLinkRefused refused ? refused.Reason : "failed", host);
            }

            PostLink(() => LinkFinishJoin(outcome));
        });
    }

    private static string LinkJoinFailure(string reason, string host) => reason switch
    {
        "invite_expired" => Said("link.devices_invite_expired_join"),
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
            linkJoinTyped = string.Empty;
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
