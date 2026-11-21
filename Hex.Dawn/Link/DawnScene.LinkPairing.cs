using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The pairing dialog (docs/hex-link-plan.md, 3.3 and 6), shown on both machines as two meet. It says who the other is
// (its name, which is only what it says, and its fingerprint, which is proved), the six digits both screens show in
// large type grouped three and three, and asks the person to check they're the same. Under that what this machine grants
// the other, a switch for each right with what it means, nothing ticked beforehand: View is what Steer and Edit stand
// on, Autocomplete stands alone (it sees no files), and Steer is said in the warning colour to be as much as sitting at
// this machine. Pair stays greyed until View or Autocomplete is ticked; Cancel declines. When the pairing ends without this side's answer (the other declined, two minutes
// passed, the connection broke) the dialog says why and closes itself a moment later.
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
        linkDirty = true;
        linkPairCloseAt = 0;

        string device = HexLinkNames.Clean(ask.Other.Name) ?? ask.Other.Fingerprint;
        UIDialog dialog = UIDialog.Show(chrome, Said("link.pair_dialog_title", ("device", device)), string.Empty,
            new DialogChoice(Said("link.pair_go"), () =>
            {
                HexLinkRights granted = LinkPairTicked;
                ask.Decide(LinkRightsAnything(granted) ? granted : null);
                linkDirty = true;
            }),
            new DialogChoice(Said("link.pair_cancel"), () =>
            {
                ask.Decide(null);
                linkDirty = true;
            }));
        linkPairingDialog = dialog;
        StackPanel body = dialog.Body;
        const float WIDE = 460.0f;

        // Who it is, and where
        body.Add(new Label
        {
            Text = Said(ask.Host ? "link.pair_who_host" : "link.pair_who_joiner", ("device", $"[b]{UIRichText.Literal(device)}[/b]"), ("address", UIRichText.Literal(ask.Address))),
            Wrap = true, Size = new Vector2(WIDE, 0), Align = Origin.TopLeft
        });
        body.Add(new Label { Text = Said("link.pair_fingerprint", ("fingerprint", ask.Other.Fingerprint)), TextScale = LINK_SMALL, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });

        // The code, large, in the accent, grouped as it's read out
        var codeRow = body.Add(new Panel { Size = new Vector2(WIDE, 76), Color = (Skin?.Palette.Field ?? new Vector4(0, 0, 0, 0.3f)) with { W = 0.5f }, Radius = 10, Outline = Skin?.BorderColor ?? Vector4.Zero });
        codeRow.Add(new Label { Text = $"[b]{LinkCodeGrouped(ask.Code)}[/b]", TextScale = LINK_CODE_SCALE, Anchor = Origin.Center, Pivot = Origin.Center, Tooltip = Said("link.pair_code_tip") });
        body.Add(new Label { Text = Said("link.pair_check"), Wrap = true, Size = new Vector2(WIDE, 0), Align = Origin.TopLeft });

        // What this machine grants, nothing ticked
        body.Add(new Label { Text = $"[b]{UIRichText.Literal(Said("link.pair_grant", ("device", device)))}[/b]", Wrap = true, Size = new Vector2(WIDE, 0), Align = Origin.TopLeft });
        Vector4 warning = LinkWarning;
        ToggleButton Right(string name, bool risky)
        {
            string label = Said("link.right_title_" + name);
            // A switch's caption is plain text, Steer's warning colour is in the line under it
            var toggle = body.Add(new ToggleButton(label, false) { Anchor = Origin.Left, Pivot = Origin.Left, Tooltip = Said("link.right_means_" + name) });
            string means = Said("link.right_means_" + name);
            body.Add(new Label
            {
                Text = risky ? $"[color:{HexColour(warning)}]$(warning) {UIRichText.Literal(means)}[/color]" : UIRichText.Literal(means),
                TextScale = LINK_SMALL, Dim = !risky, Wrap = true, Size = new Vector2(WIDE - 40, 0), Align = Origin.TopLeft, Padding = new UIEdges(40, 0, 0, 0)
            });
            return toggle;
        }

        linkPairView = Right("view", false);
        linkPairSteer = Right("steer", true);
        linkPairEdit = Right("edit", false);
        linkPairPredict = Right("predict", false);
        linkPairEnded = body.Add(new Label { Text = string.Empty, Wrap = true, Size = new Vector2(WIDE, 0), Align = Origin.TopLeft, Visible = false });

        // Steer and Edit stand on View: ticking one ticks View, taking View away takes them, and Pair waits for View
        void Flipped(HexLinkRights right, ToggleButton toggle)
        {
            HexLinkRights now = LinkRightsToggled(LinkPairTicked & ~right | (toggle.State ? HexLinkRights.None : right), right, toggle.State);
            linkPairView!.State = (now & HexLinkRights.View) != 0;
            linkPairSteer!.State = (now & HexLinkRights.Steer) != 0;
            linkPairEdit!.State = (now & HexLinkRights.Edit) != 0;
            linkPairPredict!.State = (now & HexLinkRights.Predict) != 0;
            dialog.Buttons[0].Enabled = LinkRightsAnything(now) && !ask.IsDecided;
        }

        linkPairView.OnPressed = () => Flipped(HexLinkRights.View, linkPairView);
        linkPairSteer.OnPressed = () => Flipped(HexLinkRights.Steer, linkPairSteer);
        linkPairEdit.OnPressed = () => Flipped(HexLinkRights.Edit, linkPairEdit);
        linkPairPredict.OnPressed = () => Flipped(HexLinkRights.Predict, linkPairPredict);
        dialog.Buttons[0].Enabled = false;
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
        Said("link.pair_reason_" + (reason is "declined-there" or "mismatch" or "closed" or "busy" or "timeout" or "unknown" or "unreachable" ? reason.Replace('-', '_') : "failed"));

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
