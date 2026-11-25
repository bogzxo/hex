using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>Document zoom belongs to notebook content; the surrounding IDE keeps its own scale.</summary>
internal sealed class NotebookScrollPanel : ScrollPanel
{
    private (UIComponent Component, float Fraction)? centre;

    // Capture before changing content sizes. Several wheel notches before the next
    // layout share this anchor rather than recapturing stale cell bounds.
    public void PreserveCentre(IEnumerable<UIComponent> cells)
    {
        if (centre is not null || Bounds.Height <= 0) return;
        float y = Bounds.Shrink(Padding).Center.Y;
        UIComponent? nearest = null;
        float distance = float.MaxValue;
        foreach (UIComponent cell in cells)
        {
            if (!cell.Visible || cell.Bounds.Height <= 0) continue;
            float gap = MathF.Abs(y - Math.Clamp(y, cell.Bounds.Min.Y, cell.Bounds.Max.Y));
            if (gap >= distance) continue;
            nearest = cell;
            distance = gap;
        }
        if (nearest is not null)
            centre = (nearest, (nearest.Bounds.Max.Y - y) / nearest.Bounds.Height);
    }

    protected override void Arrange(UIRect content)
    {
        base.Arrange(content);
        if (centre is not { } anchor) return;
        centre = null;
        if (anchor.Component.Module != Module || !anchor.Component.Visible) return;
        float y = anchor.Component.Bounds.Max.Y - anchor.Fraction * anchor.Component.Bounds.Height;
        float before = Offset;
        Offset += content.Center.Y - y;
        if (MathF.Abs(Offset - before) > 0.001f) base.Arrange(content);
    }

    public Action<float>? Zoom { get; set; }

    /// <summary>A pinch zooms by this factor, a smart zoom goes between the usual size and a bigger one.</summary>
    public Action<float>? Pinch { get; set; }
    public Action? SmartZoom { get; set; }

    protected override bool OnMagnify(float amount)
    {
        if (Pinch is not { } pinch)
            return false;
        pinch(Horizon.Input.PreciseScroll.PinchFactor(amount));
        return true;
    }

    protected override bool OnSmartMagnify()
    {
        if (SmartZoom is not { } smart)
            return false;
        smart();
        return true;
    }
    protected override bool UsesScrollSpeed => GameEngine.Instance?.Input.Keyboard.Primary != true;

    protected override bool OnScroll(float delta)
    {
        if (GameEngine.Instance?.Input.Keyboard.Primary == true && Zoom is { } zoom)
        {
            zoom(Horizon.Input.PreciseScroll.ZoomSteps(delta, PreciseWheel));
            return true;
        }
        return base.OnScroll(delta);
    }
}
