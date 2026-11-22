using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private StackPanel? linkEditorNotices;
    private bool linkEditorNoticesDirty = true;
    private (HexDocument? Document, float Width, LinkClaimMark? Claim, LinkSessionNotice? Session, string? Following) linkEditorNoticeDrawn;
    private float linkEditorNoticeWidth;

    private void BuildLinkEditorNotices()
    {
        if (crumbs.Parent is not StackPanel head)
            return;
        linkEditorNotices = head.Add(new StackPanel { Visible = false, Fill = UIFill.Horizontal, Spacing = 4 });
        LinkChanged += LinkEditorNoticesChanged;
    }

    private void LinkEditorNoticesChanged()
    {
        MarkLinkEditorNotices();
        LinkRefreshClaimSurfaces();
    }

    partial void LinkRefreshClaimSurfaces();

    private void MarkLinkEditorNotices() => linkEditorNoticesDirty = true;

    private void FitLinkEditorNotices(float width)
    {
        if (linkEditorNotices is not { } notices || !linkEditorNoticesDirty && linkEditorNoticeWidth == width)
            return;

        linkEditorNoticesDirty = false;
        linkEditorNoticeWidth = width;
        HexDocument? shown = plainShown ? null : document;
        LinkClaimMark? claim = LinkClaimMarkOf(shown?.Path);
        LinkSessionNotice? session = null;
        if (shown is not null)
            LinkReadSessionNotice(shown, ref session);
        var pieces = (shown, width, claim, session, session is null ? null : linkFollowing);
        if (pieces == linkEditorNoticeDrawn)
            return;

        linkEditorNoticeDrawn = pieces;
        ClearRows(notices);
        notices.Size = new Vector2(width, 0);
        notices.Visible = claim is not null || session is not null;

        if (session is not null && shown is not null)
        {
            DrawLinkSessionNotice(notices, shown, session, claim, width);
        }
        else if (claim is not null && shown?.Path is { } path)
        {
            Button? open = null;
            string caption = LinkClaimLabel(claim);
            if (claim.CanOpen)
            {
                open = LinkButton(Said("link.devices_open_shared"), "link", () => OpenLinkClaim(path));
                open.Tooltip = Said("link.devices_claim_open_tip");
            }
            else
                caption += ". " + Said("link.devices_claim_cannot_open");
            DrawEditorLinkNotice(notices, caption, "lock", warning: true, width, open);
        }
    }

    private StackPanel DrawEditorLinkNotice(StackPanel notices, string caption, string icon, bool warning, float width, Button? action = null)
    {
        const float inset = 22.0f, gap = 8.0f;
        var card = notices.Add(new Panel
        {
            Color = Skin?.Palette.Panel ?? Vector4.Zero, Outline = Skin?.BorderColor ?? Vector4.Zero,
            Radius = 6, Fill = UIFill.Horizontal, Padding = new UIEdges(10, 5)
        });
        var body = card.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 4 });
        bool inline = action is not null && width - inset - action.Size.X - gap >= 220;
        var row = body.Add(new StackPanel
        {
            Direction = inline ? UIDirection.Horizontal : UIDirection.Vertical,
            Fill = UIFill.Horizontal, Spacing = gap
        });
        Vector4 color = warning ? LinkWarning : Skin?.Palette.Text ?? Vector4.One;
        float textWidth = MathF.Max(1, width - inset - (inline ? action!.Size.X + gap : 0));
        row.Add(new Label
        {
            Text = $"[color:{HexColour(color)}]$({icon})[/color] {UIRichText.Literal(caption)}",
            Size = new Vector2(textWidth, 0), TextScale = LINK_SMALL, Wrap = true,
            Align = Origin.Left, Pivot = Origin.Left, Anchor = Origin.Left
        });
        if (action is not null)
            row.Add(action);
        return body;
    }
}
