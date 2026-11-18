using System.Numerics;

using Horizon.UI;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

/// <summary>
/// A bar cut into the parts of a whole, each its share of the width in its own colour with a hair of a gap between them,
/// the way the performance overlay cuts the GPU's frame into its passes. What the harness's window shows each tool's
/// part of the saving with.
/// </summary>
internal sealed class HexStackBar : UIComponent
{
    /// <summary>The parts, how much of the whole each is and its colour, in the order they're drawn from the left.</summary>
    public IReadOnlyList<(double Share, Vector4 Colour)> Parts { get; set; } = [];

    public Vector4 Track { get; set; } = new(1, 1, 1, 0.08f);

    protected override void Paint(UIDrawList list)
    {
        UIRect bounds = Bounds;
        float round = MathF.Min(3.0f, bounds.Height / 2);
        list.RoundRect(bounds, round, Track);
        float at = bounds.Min.X;
        foreach (var (share, colour) in Parts)
        {
            float wide = (float)(bounds.Width * Math.Clamp(share, 0.0, 1.0));
            if (wide < 1.0f)
                continue;
            // A pixel left between two parts, so neighbours of near colours still read as two
            float drawn = MathF.Max(1.0f, wide - 1.0f);
            list.RoundRect(new UIRect(new Vector2(at, bounds.Min.Y), new Vector2(MathF.Min(bounds.Max.X, at + drawn), bounds.Max.Y)), round, colour);
            at += wide;
        }
    }
}
