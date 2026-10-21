using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Hex.Dawn;

// The canvas: zooming and panning about the layout, the screen it's previewed on, and what the layout says it's made for.
internal sealed partial class HexDesigner
{
    // How much a notch of the wheel zooms by, and how far in and out it goes
    internal const float ZOOM_STEP = 1.15f;
    private const float MIN_ZOOM = 0.1f, MAX_ZOOM = 16.0f;

    // The edges of the layout, and of the screen it's previewed on when that's another shape
    private static readonly Vector4 FrameColor = new(0.38f, 0.62f, 1.0f, 0.9f);

    // Screens a layout can be made for, the usual suspects
    private static readonly Vector2[] Designs = [new(1600, 900), new(1920, 1080), new(1280, 720), new(2560, 1080), new(1080, 1920)];

    // Shapes of screen to try a layout out on: its own, and the ones that break layouts made for 16:9
    private static readonly (string Menu, string Notice, float Aspect)[] Previews =
    [
        ("designer_canvas.preview_on_its_own_shape", "designer_canvas.previewing_on_its_own_shape", 0.0f),
        ("designer_canvas.preview_on_16_9", "designer_canvas.previewing_on_16_9", 16.0f / 9.0f),
        ("designer_canvas.preview_on_21_9", "designer_canvas.previewing_on_21_9", 21.0f / 9.0f),
        ("designer_canvas.preview_on_32_9", "designer_canvas.previewing_on_32_9", 32.0f / 9.0f),
        ("designer_canvas.preview_on_4_3", "designer_canvas.previewing_on_4_3", 4.0f / 3.0f),
        ("designer_canvas.preview_on_9_16", "designer_canvas.previewing_on_9_16", 9.0f / 16.0f)
    ];

    // How far past fitting the screen into the canvas it's zoomed in, and how far it's been dragged about (in the editor's units)
    internal float canvasZoom = 1.0f;
    private Vector2 canvasPan;
    private float previewAspect;

    // Dragging the canvas about with the middle button, or space and the left one
    private bool panning;
    private Vector2 panLast;

    /// <summary>
    /// Helper method to work out the screen the layout is previewed on, in the layout's own units: the one it's made for,
    /// or that stretched into the shape that's being tried out (as tall as it, or as wide, whichever keeps it whole).
    /// </summary>
    private UIRect PreviewScreen()
    {
        Vector2 design = document.Design ?? DesignSize;
        if (previewAspect <= 0.0f)
            return UIRect.FromCenter(Vector2.Zero, design);

        float own = design.X / design.Y;
        Vector2 size = previewAspect >= own
            ? new Vector2(design.Y * previewAspect, design.Y)
            : new Vector2(design.X, design.X / previewAspect);

        return UIRect.FromCenter(Vector2.Zero, size);
    }

    /// <summary>
    /// Helper method to put the layout in the canvas: the preview screen fitted into it, then zoomed and panned however
    /// it's been, and cut off at the edges of the canvas.
    /// </summary>
    internal void PlaceCanvas()
    {
        UIRect area = canvas.Bounds;
        if (!canvas.Visible || area.IsEmpty)
            return;

        UIRect screen = PreviewScreen();
        float scale = CanvasFit(area, screen) * canvasZoom;

        // In the units of the editor's layout, the UI scales the two of them together
        var module = document.Module;
        module.Viewport = screen;
        module.Scale = new Vector2(scale);
        module.Position = area.Center + canvasPan;
        module.Clip = new UIRect((area.Min - module.Position) / scale, (area.Max - module.Position) / scale);
        module.FrameColor = FrameColor;
    }

    private static float CanvasFit(UIRect area, UIRect screen) => MathF.Min(area.Width / screen.Width, area.Height / screen.Height);

    /// <summary>
    /// Helper method to zoom the canvas by a factor, keeping whatever is under a point (in the editor's units) where it is.
    /// </summary>
    internal void ZoomCanvas(float factor, Vector2 around)
    {
        var module = document.Module;
        float before = module.Scale.X;
        if (before <= 0.0f)
            return;

        float zoom = Math.Clamp(canvasZoom * factor, MIN_ZOOM, MAX_ZOOM);
        float after = before / canvasZoom * zoom;

        // What's under the point stays under it: the layout moves the other way as it grows
        Vector2 local = (around - module.Position) / before;
        canvasPan = around - local * after - canvas.Bounds.Center;
        canvasZoom = zoom;

        context.Notices.Say(context.Strings.Said("designer_canvas.zoomed_to", ("percent", (after * 100.0f / compositor.Scale).ToString("0"))));
    }

    internal void ZoomToFit()
    {
        canvasZoom = 1.0f;
        canvasPan = Vector2.Zero;
        context.Notices.Say(context.Strings.Said("designer_canvas.zoomed_to_fit"));
    }

    internal void ZoomToActualSize()
    {
        // A unit of the layout a unit of the editor, which is a pixel at the editor's own scale of 1
        float fit = CanvasFit(canvas.Bounds, PreviewScreen());
        canvasZoom = fit > 0.0f ? 1.0f / fit : 1.0f;
        canvasPan = Vector2.Zero;
        context.Notices.Say(context.Strings.Said("designer_canvas.actual_size"));
    }

    /// <summary>
    /// Helper method to zoom with the wheel and pan with the middle button (or space and the left one) while the pointer is
    /// over the canvas. True while the canvas is being panned, which is all the pointer is doing then.
    /// </summary>
    private bool UpdateCanvasView(Vector2 inChrome, bool overCanvas, bool pressed)
    {
        var mouse = window.Mouse;
        var keyboard = context.Keyboard;

        if (overCanvas && mouse.Scroll != 0.0f && chrome.Popup is null)
            ZoomCanvas(MathF.Pow(ZOOM_STEP, mouse.Scroll), inChrome);

        bool spaceDrag = keyboard.IsDown(Key.Space) && compositor.Focus is null && stage.Focus is null;
        bool held = mouse.IsDown(MouseButton.Middle) || (spaceDrag && compositor.Pointer.Down);

        if (panning)
        {
            if (!held)
            {
                panning = false;
                return false;
            }

            canvasPan += inChrome - panLast;
            panLast = inChrome;
            return true;
        }

        if (overCanvas && held && (mouse.WasPressed(MouseButton.Middle) || pressed))
        {
            panning = true;
            panLast = inChrome;
            return true;
        }

        return false;
    }

    /* What the layout is made for */

    private void SetDesign(Vector2? size, UIFit fit)
    {
        document.SetDesign(size, fit);
        codeDirty = inspectorDirty = true;

        context.Notices.Say(size is { } made
            ? context.Strings.Said(fit == UIFit.Contain ? "designer_canvas.made_for_contain" : "designer_canvas.made_for_stretch",
                ("width", made.X.ToString("0")), ("height", made.Y.ToString("0")))
            : context.Strings.Said("designer_canvas.made_for_any_screen"));
    }

    internal static string DescribeDesign(Vector2 size) =>
        Hex.Shell.HexText.Get("designer_canvas.design_for", ("width", size.X.ToString("0")), ("height", size.Y.ToString("0")));

    /// <summary>What screen the layout is made for, how it goes on another one, and what it is previewed on, the Layout menu's Screen.</summary>
    private void FillScreenMenu(Menu made)
    {
        made.Add(context.Strings.Said("designer_canvas.any_screen"), () => SetDesign(null, document.Fit)).IsChecked = () => document.Design is null;
        foreach (Vector2 size in Designs)
            made.Add(DescribeDesign(size), () => SetDesign(size, document.Fit)).IsChecked = () => document.Design == size;

        made.AddSeparator();
        made.Add(context.Strings.Said("designer_canvas.contain"), () => SetDesign(document.Design ?? DesignSize, UIFit.Contain)).IsChecked =
            () => document.Design is not null && document.Fit == UIFit.Contain;
        made.Add(context.Strings.Said("designer_canvas.stretch"), () => SetDesign(document.Design ?? DesignSize, UIFit.Stretch)).IsChecked =
            () => document.Design is not null && document.Fit == UIFit.Stretch;

        made.AddSeparator();
        foreach (var (menu, notice, aspect) in Previews)
        {
            made.Add(context.Strings.Said(menu), () =>
            {
                previewAspect = aspect;
                context.Notices.Say(context.Strings.Said(notice));
            }).IsChecked = () => previewAspect == aspect;
        }
    }

    // What the hover is being shown on in the canvas, what is under the pointer and everything it is inside of
    private readonly List<UIComponent> heldHovers = [];

    /// <summary>
    /// Helper method to show the hover of whatever is under the pointer in the canvas. The layout being edited is never
    /// handed the pointer, it is being edited and not used, so the hover wouldn't come on by itself. A card that lights
    /// up when one of its buttons is pointed at does here too, everything the thing is inside of is held with it.
    /// </summary>
    private void HoldHover(UIComponent? under)
    {
        foreach (var held in heldHovers)
            held.HoverHeld = false;
        heldHovers.Clear();

        for (UIComponent? at = under; at is not null; at = at.Parent)
        {
            at.HoverHeld = true;
            heldHovers.Add(at);
        }
    }

    /// <summary>The canvas a step closer, or further, round its middle.</summary>
    internal void ZoomCanvasIn() => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center);
    internal void ZoomCanvasOut() => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center);
}
