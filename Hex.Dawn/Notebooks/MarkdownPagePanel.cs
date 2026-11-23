using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

/// <summary>
/// The sheet a markdown preview is written on: the stack spans the whole scroll view, so the wheel works over the
/// margins too, while the paper is painted only between <see cref="Margin"/> from either side. The padding of the
/// stack is set by its owner to the margin plus the inner padding.
/// </summary>
internal sealed class MarkdownPagePanel : StackPanel
{
    /// <summary>The distance from each side of the view to the edge of the page.</summary>
    public float Margin { get; set; }

    /// <summary>The space above and below the page.</summary>
    public float Gap { get; set; } = 16;

    protected override void Paint(UIDrawList list)
    {
        var skin = list.Skin;
        var page = new UIRect(
            new Vector2(Bounds.Min.X + Margin, Bounds.Min.Y + Gap),
            new Vector2(Bounds.Max.X - Margin, Bounds.Max.Y - Gap));
        if (page.Width <= 0 || page.Height <= 0) return;

        // A soft shadow, a few rings each fainter than the one inside it
        Vector4 shade = new(0, 0, 0, 0.07f);
        for (int ring = 3; ring >= 1; ring--)
            list.RoundRect(new UIRect(page.Min + new Vector2(-ring * 2, -ring * 2 - 2), page.Max + new Vector2(ring * 2, ring * 2 - 2)), 4 + ring * 2, shade);
        list.RoundRect(page, 3, Color ?? skin.FieldColor);
        list.RoundOutline(page, 3, 1, skin.BorderColor);
    }
}
