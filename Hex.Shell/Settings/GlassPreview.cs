using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Shell;

/// <summary>
/// The glass as the four sliders of the effects make it, shown under them in the settings so a slider can be watched
/// as it is dragged. A popup on the popups' glass and a side panel on the sides' over bands of colour, round blobs and
/// big letters, which is what shows a blur, how much comes through, the rim round the edge and the lens bending what
/// is under it. With the glass off they're the solid panels they are then, and it says so.
/// </summary>
public sealed class GlassPreview : UIComponent
{
    // The colours behind, loud on purpose, glass over a plain panel looks like no glass at all
    private static readonly Vector4[] Bands =
    [
        new(0.93f, 0.33f, 0.31f, 1.0f), new(0.98f, 0.69f, 0.23f, 1.0f), new(0.36f, 0.78f, 0.45f, 1.0f),
        new(0.25f, 0.62f, 0.95f, 1.0f), new(0.62f, 0.42f, 0.93f, 1.0f), new(0.95f, 0.45f, 0.72f, 1.0f)
    ];

    private const float HEIGHT = 170.0f, CARD_TEXT = 0.75f;

    /// <summary>The popups' glass and the sides' as the sliders have them now, and whether the glass is on at all. Asked every paint.</summary>
    public Func<(UIGlass Popup, UIGlass Side, bool On)>? Look { get; set; }

    public GlassPreview(float width)
    {
        Size = new Vector2(width, HEIGHT);
    }

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect area = Bounds.Shrink(new UIEdges(14.0f, 6.0f, 10.0f, 6.0f));
        if (area.IsEmpty || Look is not { } look)
            return;

        var (popup, side, on) = look();

        // What the glass is over, bands of colour, blobs across them and letters bigger than any on a panel
        list.PushClip(area);
        float band = area.Width / Bands.Length;
        for (int i = 0; i < Bands.Length; i++)
            list.Rect(new UIRect(new Vector2(area.Min.X + i * band, area.Min.Y), new Vector2(area.Min.X + (i + 1) * band, area.Max.Y)), Bands[i]);

        for (int i = 0; i < 7; i++)
        {
            float size = 26.0f + 14.0f * (i % 3);
            var at = new Vector2(area.Min.X + area.Width * (0.08f + 0.14f * i), area.Min.Y + area.Height * (i % 2 == 0 ? 0.3f : 0.7f));
            list.RoundRect(new UIRect(at - new Vector2(size * 0.5f), at + new Vector2(size * 0.5f)), size * 0.5f, i % 2 == 0 ? new Vector4(1.0f, 1.0f, 1.0f, 0.85f) : new Vector4(0.05f, 0.06f, 0.1f, 0.8f));
        }

        list.Text(HexText.Get("glass_preview.backdrop"), area, Origin.Center, skin.TextScale * 3.2f, new Vector4(1.0f, 1.0f, 1.0f, 0.9f), markup: false);
        list.PopClip();

        // A side panel down the left on the sides' glass, a popup in the middle on the popups'
        var sideRect = new UIRect(area.Min + new Vector2(16.0f, 16.0f), new Vector2(area.Min.X + area.Width * 0.24f, area.Max.Y - 16.0f));
        if (on)
            list.Glass(sideRect, 0.0f, side.Blur, 0.0f);
        list.Rect(sideRect, on ? side.Over(skin.PanelColor with { W = 1.0f }) : skin.PanelColor with { W = 1.0f });
        if (on && side.Rim > 0.0f)
            list.SilverLining(sideRect, 0.0f, side.Rim);
        list.Text(HexText.Get("glass_preview.side"), sideRect.Shrink(new UIEdges(10.0f)), Origin.TopLeft, skin.TextScale * CARD_TEXT, skin.TextColor, markup: false);

        var popupRect = new UIRect(new Vector2(area.Min.X + area.Width * 0.36f, area.Min.Y + 26.0f), new Vector2(area.Max.X - area.Width * 0.08f, area.Max.Y - 26.0f));
        list.Popup(popupRect, on ? popup : null);
        UIRect inside = popupRect.Shrink(new UIEdges(14.0f, 10.0f, 14.0f, 10.0f));
        list.Text(on ? HexText.Get("glass_preview.popup_on") : HexText.Get("glass_preview.glass_off"), inside, Origin.TopLeft, skin.TextScale * CARD_TEXT * 1.15f, skin.TextColor, markup: false);
        list.Text(on
                ? HexText.Get("glass_preview.values", ("blur", popup.Blur.ToString("0")), ("clarity", popup.Clarity.ToString("0.00")), ("rim", popup.Rim.ToString("0.00")), ("bend", popup.Bend.ToString("0.0")))
                : HexText.Get("glass_preview.off_reason"),
            inside, Origin.BottomLeft, skin.TextScale * CARD_TEXT * 0.85f, skin.TextColor with { W = skin.TextColor.W * 0.7f }, markup: false);
    }
}
