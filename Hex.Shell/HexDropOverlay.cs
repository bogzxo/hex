using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Shell;

/// <summary>
/// What letting go of files dragged over the window would do, said while they're held: a hairline in the look's accent
/// round the place they'd land (the whole window, a folder's row in the explorer, a terminal's pane) with the faintest
/// wash of it inside, and one line in a pill along the bottom of that (beside it, for a row), "Open 3 files", "Copy 2 items into src". Nothing
/// moves and nothing is dimmed, the window stays as it is under it. Hidden while nothing is held.
/// </summary>
public sealed class HexDropOverlay : UIComponent
{
    // The hairline, its corners and how far in from the edge of the place, the wash and the pill, in the UI's units
    private const float INSET = 6.0f, WINDOW_INSET = 3.0f, RADIUS = 8.0f, LINE = 1.5f, WASH = 0.06f, PILL_GAP = 14.0f, PILL_PAD = 14.0f, ICON = 16.0f;

    /// <summary>What letting go would do, in a few words.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>The icon in front of it, one every skin has (UISymbols).</summary>
    public string Icon { get; private set; } = "files";

    /// <summary>Where the files would land, in the module's units, null for the whole of it.</summary>
    public UIRect? Place { get; private set; }

    public HexDropOverlay()
    {
        Fill = UIFill.Both;
        Visible = false;
    }

    /// <summary>Says what letting go would do and where, shown from now on.</summary>
    public void Show(string text, string icon, UIRect? place)
    {
        Text = text;
        Icon = icon;
        Place = place;
        Visible = text.Length > 0;
    }

    /// <summary>Nothing held any more.</summary>
    public void Hide()
    {
        Visible = false;
        Text = string.Empty;
        Place = null;
    }

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Paint(UIDrawList list)
    {
        if (!Visible || Text.Length == 0)
            return;

        UISkin skin = list.Skin;
        UIRect whole = Bounds;
        UIRect place = Place is { } given ? given : whole.Shrink(new UIEdges(WINDOW_INSET));
        if (place.IsEmpty)
            return;

        // The place they'd land, a hairline round it and the accent barely there inside
        Vector4 accent = skin.AccentColor;
        float radius = MathF.Min(RADIUS, MathF.Min(place.Width, place.Height) * 0.5f);
        list.RoundRect(place, radius, accent with { W = accent.W * WASH });
        list.RoundOutline(place, radius, LINE, accent with { W = accent.W * 0.8f });

        // The pill, under the middle of the place, or over it when the place is low in the window (a row near the bottom)
        float scale = skin.TextScale;
        Vector2 text = skin.Font.Measure(Text, scale, markup: false);
        float height = MathF.Max(text.Y, ICON) + PILL_PAD;
        float width = MathF.Min(whole.Width - 2.0f * INSET, text.X + ICON + PILL_PAD * 2.0f + PILL_PAD * 0.5f);
        float x, y;
        if (Place is not null && place.Height < height * 3.0f)
        {
            // A row: the pill beside it, where it hides none of the rows round it, on whichever side there's room
            x = place.Max.X + PILL_GAP * 0.5f + width <= whole.Max.X - INSET ? place.Max.X + PILL_GAP * 0.5f : place.Min.X - PILL_GAP * 0.5f - width;
            y = place.Center.Y - height * 0.5f;
        }
        else
        {
            // The UI's Y goes up: the window or a pane has its pill along the bottom inside it
            x = place.Center.X - width * 0.5f;
            y = place.Min.Y + PILL_GAP;
        }

        x = Math.Clamp(x, whole.Min.X + INSET, MathF.Max(whole.Min.X + INSET, whole.Max.X - INSET - width));
        y = Math.Clamp(y, whole.Min.Y + INSET, MathF.Max(whole.Min.Y + INSET, whole.Max.Y - INSET - height));
        var pill = new UIRect(new Vector2(x, y), new Vector2(x + width, y + height));

        list.RoundRect(pill, height * 0.5f, skin.PanelColor with { W = 1.0f });
        list.RoundOutline(pill, height * 0.5f, 1.0f, skin.BorderColor);
        var icon = new UIRect(new Vector2(pill.Min.X + PILL_PAD, pill.Center.Y - ICON * 0.5f), new Vector2(pill.Min.X + PILL_PAD + ICON, pill.Center.Y + ICON * 0.5f));
        list.Symbol(Icon, icon, accent);
        var words = new UIRect(new Vector2(icon.Max.X + PILL_PAD * 0.5f, pill.Min.Y), new Vector2(pill.Max.X - PILL_PAD, pill.Max.Y));
        list.Text(Text, words, Origin.Left, scale, skin.TextColor, markup: false);
    }
}
