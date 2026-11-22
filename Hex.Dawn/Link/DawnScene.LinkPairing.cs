using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Pairing confirmation on each device. Address pairing shows the comparison code. Invites show verified identity.
internal sealed partial class DawnScene
{
    // How large the code is written, and how long a pairing that ended is said in the dialog before it closes
    private const float LINK_CODE_SCALE = 0.48f;
    private const long LINK_ENDED_SHOWN_MS = 2500;

    // The switches of the dialog up, the line that says why it ended and when it closes by itself after that
    private ToggleButton? linkPairView, linkPairSteer, linkPairEdit, linkPairPredict;
    private Label? linkPairEnded;
    private long linkPairCloseAt;

    /// <summary>The code as it's read out, three digits, a gap, three digits.</summary>
    internal static string LinkCodeGrouped(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    /// <summary>What's ticked in the pairing dialog, nothing when it isn't up. For the self test and the Pair button.</summary>
    internal HexLinkRights LinkPairTicked =>
        (linkPairView?.State == true ? HexLinkRights.View : HexLinkRights.None)
        | (linkPairSteer?.State == true ? HexLinkRights.Steer : HexLinkRights.None)
        | (linkPairEdit?.State == true ? HexLinkRights.Edit : HexLinkRights.None)
        | (linkPairPredict?.State == true ? HexLinkRights.Predict : HexLinkRights.None);

    /// <summary>A pairing asked about with the dialog, one up already for another pairing declined and closed first.</summary>
    private void ShowLinkPairing(LinkPairingAsk ask)
    {
        if (ask.IsDecided)
            return;

        linkPairingPending?.Decide(null);
        CloseLinkPairingDialog();
        linkPairingPending = ask;
        linkReopenJoin = !ask.Host && linkJoining && linkDevicePage == LinkDevicePage.Join;
        linkDirty = true;
        linkPairCloseAt = 0;

        string device = HexLinkNames.Clean(ask.Other.Name) ?? ask.Other.Fingerprint;
        UIDialog dialog = UIDialog.Show(chrome, Said("link.pair_dialog_title", ("device", device)), string.Empty,
            new DialogChoice(Said("link.pair_go"), () =>
            {
                HexLinkRights granted = LinkPairTicked;
                if (ask.ShareOnJoin && (granted & HexLinkRights.View) != 0)
                    linkShareAfterPairing = ask.Other.Id;
                ask.Decide(ask.CanApprove(granted) ? granted : null);
                linkDirty = true;
            }),
            new DialogChoice(Said("link.pair_cancel"), () =>
            {
                ask.Decide(null);
                linkDirty = true;
            }));
        linkPairingDialog = dialog;
        foreach (Button button in dialog.Buttons)
        {
            button.Style = string.Empty;
            button.Animated = false;
        }
        StackPanel body = dialog.Body;
        float wide = Math.Clamp(chrome.Root.Bounds.Width - 80, 350, 460);
        body.Size = new Vector2(wide, 0);
        body.Spacing = 7;

        // Who it is, and where
        body.Add(new Label
        {
            Text = Said(ask.Host ? "link.pair_who_host" : "link.pair_who_joiner", ("device", $"[b]{UIRichText.Literal(device)}[/b]"), ("address", UIRichText.Literal(ask.Address))),
            Wrap = true, Size = new Vector2(wide, 0), Align = Origin.TopLeft, Anchor = Origin.Left
        });
        body.Add(new Label { Text = Said("link.pair_fingerprint", ("fingerprint", ask.Other.Fingerprint)), TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });

        if (ask.Invited)
            body.Add(LinkLine(Said("link.devices_invite_verified")));
        else
        {
            var codeRow = body.Add(new Panel { Size = new Vector2(wide, 64), Anchor = Origin.Left, Color = (Skin?.Palette.Field ?? new Vector4(0, 0, 0, 0.3f)) with { W = 0.5f }, Radius = 8, Outline = Skin?.BorderColor ?? Vector4.Zero });
            codeRow.Add(new Label { Text = $"[b]{LinkCodeGrouped(ask.Code)}[/b]", TextScale = LINK_CODE_SCALE, Anchor = Origin.Center, Pivot = Origin.Center, Tooltip = Said("link.pair_code_tip") });
            body.Add(new Label { Text = Said("link.pair_check"), Wrap = true, Size = new Vector2(wide, 0), Align = Origin.TopLeft, Anchor = Origin.Left });
        }

        body.Add(new Label { Text = $"[b]{UIRichText.Literal(Said("link.pair_grant", ("device", device)))}[/b]", Wrap = true, Size = new Vector2(wide, 0), Align = Origin.TopLeft, Anchor = Origin.Left });
        ToggleButton[] toggles = LinkGrantControls(body, ask.Preset ?? HexLinkPreset.Custom, ask.InitialRights,
            (_, rights) => dialog.Buttons[0].Enabled = ask.CanApprove(rights) && !ask.IsDecided,
            showBoth: true, hostOffer: ask.Host ? null : ask.Terms?.HostGrants, allowNone: ask.CanApproveNone,
            partnerName: device, requested: ask.Host ? ask.Terms?.HostRequests : null);
        linkPairView = toggles[0];
        linkPairSteer = toggles[1];
        linkPairEdit = toggles[2];
        linkPairPredict = toggles[3];
        linkPairEnded = body.Add(new Label { Text = string.Empty, Wrap = true, Size = new Vector2(wide, 0), Align = Origin.TopLeft, Anchor = Origin.Left, Visible = false });
        dialog.Buttons[0].Enabled = ask.CanApprove(ask.InitialRights);
        dialog.Closed = () =>
        {
            // Closed however it was (Escape is Cancel), never left waiting. On the next tick: a button's Picked runs after
            // the dialog has closed, and Pair's answer is the one that counts
            PostLink(() => ask.Decide(null));
            if (linkPairingDialog == dialog)
                linkPairingDialog = null;
        };

        // The pairing ending without this side's answer said in the dialog, then it closes
        ask.Ended.Register(() => PostLink(() => LinkPairingEndedThere(dialog)));
    }

    // A pairing that ended while its dialog was up: why, in the dialog, its buttons let go of, closed a moment later
    private void LinkPairingEndedThere(UIDialog dialog)
    {
        if (!dialog.IsOpen || linkPairingDialog != dialog || linkPairEnded is null)
            return;
        linkPairEnded.Text = $"[color:{HexColour(LinkWarning)}]$(warning) {UIRichText.Literal(Said("link.pair_ended"))}[/color]";
        linkPairEnded.Visible = true;
        dialog.Buttons[0].Enabled = false;
        linkPairCloseAt = System.Environment.TickCount64 + LINK_ENDED_SHOWN_MS;
    }

    // Every tick while the dialog's up: one whose pairing ended is closed once it's been read
    private void UpdateLinkPairingDialog()
    {
        if (linkPairCloseAt != 0 && System.Environment.TickCount64 >= linkPairCloseAt)
        {
            linkPairCloseAt = 0;
            CloseLinkPairingDialog();
        }
    }

    /// <summary>Why a pairing ended in words, for the dialog and the notice.</summary>
    private static string LinkPairReasonSaid(string? reason) =>
        Said("link.pair_reason_" + (reason is "declined-there" or "mismatch" or "closed" or "busy" or "timeout" or "unknown" or "unreachable" or "public_disabled" or "invite_expired" or "invite_invalid" or "version" or "pin" ? reason.Replace('-', '_') : "failed"));

    private void CloseLinkPairingDialog()
    {
        if (linkPairingDialog is { IsOpen: true } open)
            open.Close();
        linkPairingDialog = null;
        linkPairView = linkPairSteer = linkPairEdit = linkPairPredict = null;
        linkPairEnded = null;
        linkPairCloseAt = 0;
    }
}
