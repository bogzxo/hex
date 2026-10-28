using System.Numerics;

namespace Hex.Installer;

internal static class InstallerLayout
{
    // Match Hex: fit in logical display units. UICompositor applies monitor DPI once, separately.
    public static float Fit(Vector2 pixels, float displayScale)
    {
        if (pixels.X <= 0 || pixels.Y <= 0 || !float.IsFinite(displayScale) || displayScale <= 0) return 1;
        Vector2 logical = pixels / displayScale;
        return Math.Clamp(Math.Min(logical.X / 980, logical.Y / 620), .1f, 1);
    }
}
