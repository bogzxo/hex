using System.Numerics;

using Horizon.UI;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

/// <summary>
/// A rounded bar filled as far as a share, painted from its own width so it follows a window being made wider or
/// narrower with nothing to put right. What the agents' windows show a share with, the harness's saving of a tool.
/// </summary>
internal sealed class HexShareBar : UIComponent
{
    /// <summary>How much of it is filled, 0 to 1.</summary>
    public float Share { get; set; }

    public Vector4 Colour { get; set; } = new(0.38f, 0.62f, 1.0f, 1.0f);

    public Vector4 Track { get; set; } = new(1, 1, 1, 0.08f);

    protected override void Paint(UIDrawList list)
    {
        UIRect bounds = Bounds;
        float round = bounds.Height / 2;
        list.RoundRect(bounds, round, Track);
        if (Share <= 0.0f)
            return;
        float wide = MathF.Max(bounds.Height, bounds.Width * Share);
        list.RoundRect(new UIRect(bounds.Min, new Vector2(bounds.Min.X + wide, bounds.Max.Y)), round, Colour);
    }
}
