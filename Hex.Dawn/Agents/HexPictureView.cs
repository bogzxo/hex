using System.Numerics;

using Horizon.Graphics;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

/// <summary>
/// A picture to look at closely, the whole of it fitted at first (a small one at its own size) and zoomed by the wheel
/// round the middle, by + and - and their buttons, 0 back to fitted and 1 to one pixel a pixel, dragged to see another
/// part of it, Escape asking for it to be closed. For the pictures in the agents' chat seen full screen.
/// </summary>
internal sealed class HexPictureView : UIComponent
{
    // The most and least it's zoomed, and by how much a step
    private const float MOST = 32.0f, LEAST = 0.05f, STEP = 1.25f;

    /// <summary>The picture, null for none.</summary>
    public Texture? Picture { get; set; }

    /// <summary>How big it's drawn, a pixel of it to so many of the screen's, 0 for fitted to the room.</summary>
    public float Zoom { get; private set; }

    /// <summary>Told when Escape is pressed or the picture is double clicked out of.</summary>
    public Action? OnClose { get; set; }

    /// <summary>Told whenever the zoom moves, for a line saying how far.</summary>
    public Action? OnZoomed { get; set; }

    // Where its middle is from the room's, moved by dragging, and where the pointer was as the drag went on
    private Vector2 offset;
    private Vector2? grabbed;

    /// <summary>How big it's drawn now, the fitted size worked out for the room it has, never bigger than itself.</summary>
    public float Shown
    {
        get
        {
            if (Zoom > 0.0f || Picture is not { Width: > 0, Height: > 0 } picture || Bounds.IsEmpty)
                return Zoom > 0.0f ? Zoom : 1.0f;
            return MathF.Min(1.0f, MathF.Min(Bounds.Width / picture.Width, Bounds.Height / picture.Height));
        }
    }

    /// <summary>Zoomed by a factor from how it's drawn now.</summary>
    public void ZoomBy(float factor)
    {
        Zoom = Math.Clamp(Shown * factor, LEAST, MOST);
        OnZoomed?.Invoke();
    }

    /// <summary>Back to fitted, in the middle.</summary>
    public void Fit()
    {
        Zoom = 0.0f;
        offset = Vector2.Zero;
        OnZoomed?.Invoke();
    }

    /// <summary>A pixel of it a pixel of the screen, in the middle.</summary>
    public void Actual()
    {
        Zoom = 1.0f;
        offset = Vector2.Zero;
        OnZoomed?.Invoke();
    }

    protected override bool HitTestVisible => true;

    protected override bool Focusable => true;

    protected override bool TakesRawKeys => true;

    protected override Vector2 Measure(UISkin skin) => Vector2.Zero;

    // The room it was last laid out in, its fitted zoom said again when that changes (first of all as it comes up)
    private Vector2 laid;

    protected override void Arrange(UIRect content)
    {
        base.Arrange(content);
        var size = new Vector2(Bounds.Width, Bounds.Height);
        if (size == laid)
            return;
        laid = size;
        OnZoomed?.Invoke();
    }

    protected override bool OnScroll(float delta)
    {
        ZoomBy(delta > 0 ? STEP : 1.0f / STEP);
        return true;
    }

    protected override void OnPointerDown(Vector2 point) => grabbed = point;

    protected override void OnPointerDrag(Vector2 point)
    {
        if (grabbed is not { } from)
            return;
        offset += point - from;
        grabbed = point;
    }

    protected override void OnPointerUp(Vector2 point) => grabbed = null;

    protected override void OnKeyDown(in UIKeyPress press)
    {
        switch (press.Key)
        {
            case Key.Escape:
                OnClose?.Invoke();
                break;
            case Key.Equal or Key.KeypadAdd:
                ZoomBy(STEP);
                break;
            case Key.Minus or Key.KeypadSubtract:
                ZoomBy(1.0f / STEP);
                break;
            case Key.D0 or Key.Keypad0:
                Fit();
                break;
            case Key.Number1 or Key.Keypad1:
                Actual();
                break;
            case Key.Left:
                offset.X += 40.0f;
                break;
            case Key.Right:
                offset.X -= 40.0f;
                break;
            case Key.Up:
                offset.Y -= 40.0f;
                break;
            case Key.Down:
                offset.Y += 40.0f;
                break;
        }
    }

    protected override void Paint(UIDrawList list)
    {
        if (Picture is not { IsValid: true } picture)
            return;
        float shown = Shown;
        var size = new Vector2(picture.Width * shown, picture.Height * shown);
        list.PushClip(Bounds);
        list.Image(picture, UIRect.FromCenter(Bounds.Center + offset, size), Vector4.One);
        list.PopClip();
    }
}
