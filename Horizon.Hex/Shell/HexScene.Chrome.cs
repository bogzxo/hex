using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// How big the editor draws itself and how it shares the window out between its parts.
internal sealed partial class HexScene
{
    // Where the editor keeps how big it was last asked to draw itself, next to its executable.
    private const string SCALE_FILE = "hex_ui_scale.txt";

    /* How big the editor draws itself */

    private static string DescribeScale(float scale) => $"{scale * 100:0}%";

    // How big the menus in the title bar are written, at a scale of one
    private const float MENU_TEXT = 0.14f;

    /* The code's zoom, apart from the editor's scale */

    private const float CODE_ZOOM_STEP = 1.1f;
    private const float MIN_CODE_ZOOM = 0.5f, MAX_CODE_ZOOM = 3.0f;

    /// <summary>Makes the code bigger or smaller by a factor, in every file that is open, 0 for the size it comes at.</summary>
    private void ZoomCode(float factor)
    {
        float zoom = factor <= 0.0f ? 1.0f : Math.Clamp(settings.CodeZoom * factor, MIN_CODE_ZOOM, MAX_CODE_ZOOM);

        // Close enough to the size it comes at is that size, a few steps in and out land back on it
        if (MathF.Abs(zoom - 1.0f) < 0.02f) zoom = 1.0f;

        settings.CodeZoom = zoom;
        foreach (HexDocument open in documents)
            open.Editor.TextScale = EDITOR_TEXT * zoom;

        SaveSettings();
        Say($"the code is at {zoom * 100.0f:0}%, Ctrl+0 puts it back");
    }

    /// <summary>The size a window is best read at when nobody has said. Bigger on a bigger screen.</summary>
    private static float ScaleFor(Vector2 window) => window.Y switch
    {
        >= 2000 => 2.0f,
        >= 1400 => 1.5f,
        >= 1000 => 1.25f,
        _ => 1.0f
    };

    private static string ScalePath => Path.Combine(AppContext.BaseDirectory, SCALE_FILE);

    private float? ReadScale()
    {
        // The editor testing itself starts out the same every time
        if (options.SelfTest || !File.Exists(ScalePath))
            return null;

        return float.TryParse(File.ReadAllText(ScalePath), System.Globalization.CultureInfo.InvariantCulture, out float saved) ? saved : null;
    }

    /// <summary>
    /// Sets how big the editor and the layouts in it are drawn. The editor makes room by itself. The lists,
    /// the canvas and the code get whatever the window has left at that size, see <see cref="FitChrome"/>.
    /// </summary>
    /// <param name="remember">Whether this is what the editor starts with from now on.</param>
    private void SetScale(float scale, bool remember)
    {
        scale = Scales.MinBy(known => MathF.Abs(known - scale));

        // The two are one screen as far as anybody looking at it is concerned
        compositor.Scale = stage.Scale = scale;

        // Except the title bar, which is the window's and the size of the system's whatever the editor is drawn at, its menus with it
        menu.TextScale = MENU_TEXT / scale;

        if (!remember || options.SelfTest)
            return;

        try
        {
            File.WriteAllText(ScalePath, scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e)
        {
            Say($"couldn't keep the scale for next time: {e.Message}", error: true);
        }
    }

    /// <summary>
    /// Gives the parts of the editor that have no size of their own the room the window has left. The list on
    /// the left and the inspector the height their columns have to spare, the canvas and the code the width
    /// between the two and the height between them. Everything else keeps the size its layout gives it.
    /// </summary>
    // How much narrower than its list a row of it is, the room the scroll bar, the gap next to it and the
    // padding take
    private const float ROW_MARGIN = 24.0f;

    // How wide the lists and the properties were the last time what is in them was fitted to that
    private float fittedLeft, fittedRight;

    /// <summary>
    /// Helper method to have what is in the panels on either side be as wide as the panels, which can be dragged
    /// wider and narrower. The rows of the lists and the buttons over them are simply given the width, the
    /// properties are put together again, every kind of editor in them shares its row out its own way.
    /// </summary>
    private void FitRows(float leftWidth, float rightWidth)
    {
        if (rightWidth != fittedRight)
        {
            fittedRight = rightWidth;
            inspectorDirty = true;
        }

        // The rows are made anew at the width their template has whenever the list changes, so they are looked
        // at every time. Counted through by number, a foreach would make something to go through them with
        float row = MathF.Max(MIN_FLEXIBLE, leftWidth - ROW_MARGIN);
        Widen(tree.Children, row);
        Widen(layers.Children, row);

        if (leftWidth == fittedLeft)
            return;

        fittedLeft = leftWidth;

        // Two to a line, with the gap of their grid between them
        float button = MathF.Floor((leftWidth - 4.0f) * 0.5f);
        for (int i = 0; i < paletteButtons.Count; i++)
            paletteButtons[i].Size = paletteButtons[i].Size with { X = button };
    }

    private static void Widen(IReadOnlyList<UIComponent> rows, float width)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Size.X != width)
                rows[i].Size = rows[i].Size with { X = width };
        }
    }

    /// <summary>Helper method to make the gap in the activity bar take what its buttons leave, so Settings is at the bottom.</summary>
    private void FitActivityGap(float height)
    {
        var gap = activityGap;
        float taken = activity.Padding.Total.Y;
        int shown = 0;
        var children = activity.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (!children[i].Visible || children[i] == gap)
                continue;

            taken += children[i].Bounds.Height;
            shown++;
        }

        // A gap either side of the gap
        taken += activity.Spacing * shown;
        float wanted = MathF.Max(0.0f, MathF.Floor(height - taken));
        if (gap.Size.Y != wanted)
            gap.Size = gap.Size with { Y = wanted };
    }

    // The most of the side bar's height the toolbox takes before it scrolls
    private const float PALETTE_SHARE = 0.42f;

    private void FitChrome()
    {
        UIRect screen = chrome.Root.Bounds;
        if (screen.IsEmpty || top.Bounds.IsEmpty)
            return;

        // Between the title bar with the menus on top and the status bar at the bottom
        float height = screen.Height - root.Padding.Total.Y - top.Bounds.Height - statusBar.Bounds.Height;
        float leftWidth = treeScroll.Size.X;
        float rightWidth = inspectorScroll.Size.X;
        float sides = activity.Bounds.Width
            + (left.Visible ? leftWidth + left.Padding.Total.X : 0.0f)
            + (right.Visible ? rightWidth + right.Padding.Total.X : 0.0f);
        float centerWidth = MathF.Max(MIN_CENTER, screen.Width - root.Padding.Total.X - sides);

        // What a column needs for everything in it that isn't given its height here, as it was last laid out. One part
        // given its height a column, a params array and a sum of it made garbage every tick
        static float Spoken(StackPanel column, UIComponent flexible) => column.Bounds.Height - flexible.Bounds.Height;

        // The activity bar runs the height of the window, the colour of it with it, and its gap pushes Settings to the bottom
        activity.Size = activity.Size with { Y = height };
        FitActivityGap(height);

        // The layers under the list are as wide as the list, whichever way that was dragged, and only as tall as the side bar
        // has room for with the list over them at its least. Dragged taller than that they pushed the whole editor down and
        // the status bar off the window, now the drag stops there
        layersScroll.Size = layersScroll.Size with { X = leftWidth };
        if (layersScroll.Visible && layersScroll.Parent is { Visible: true })
        {
            float spare = height - Spoken(left, treeScroll) - MIN_FLEXIBLE;
            float most = Math.Clamp(layersScroll.Size.Y + spare, MIN_LAYERS, MAX_LAYERS);
            layersScroll.MaxSize = new Vector2(0.0f, most);
            if (layersScroll.Size.Y > most)
                layersScroll.Size = layersScroll.Size with { Y = most };
        }

        // The toolbox is all of its buttons when there is room, and scrolls in no more than a share of the side bar when there isn't
        float paletteTall = palette.DesiredSize.Y;
        paletteScroll.Size = new Vector2(leftWidth, MathF.Min(paletteTall, MathF.Max(MIN_FLEXIBLE, height * PALETTE_SHARE)));
        if (treeScroll.Visible)
            treeScroll.Size = new Vector2(leftWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(left, treeScroll)));
        if (sideTree.Visible)
            sideTree.Size = new Vector2(leftWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(left, sideTree)));
        inspectorScroll.Size = new Vector2(rightWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(right, inspectorScroll)));
        rightHead.Size = new Vector2(rightWidth, 24.0f);
        if (valuesTree.Visible)
            valuesTree.Size = new Vector2(rightWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(right, valuesTree)));

        FitRows(leftWidth, rightWidth);

        // The panel under the editor is as tall as it was dragged, as long as that leaves the editor or the canvas their least
        if (bottom.Visible)
        {
            UIComponent shown = LayoutTools ? canvas : codeScroll;
            float spare = height - Spoken(center, shown) - MIN_FLEXIBLE;
            float most = Math.Clamp(bottom.Size.Y + spare, MIN_PANEL, MAX_PANEL);
            bottom.MaxSize = new Vector2(0.0f, most);
            if (bottom.Size.Y > most)
                bottom.Size = bottom.Size with { Y = most };
        }

        // The tabs, the breadcrumbs and the panel are the width of the middle, the panel as tall as it was dragged
        docTabs.Size = docTabs.Size with { X = MathF.Max(MIN_FLEXIBLE, centerWidth - toolbar.Bounds.Width) };
        crumbs.Size = crumbs.Size with { X = centerWidth };
        bottom.Size = bottom.Size with { X = centerWidth };
        FitRowsTo(problems, problemsScroll);
        FitConsole();

        // Whichever of the two is showing has the rest of the middle, the code or the canvas of the designer
        // The table's sideways bar runs along its bottom, which is where the panel's drag tab reaches up to, so it runs that
        // much higher
        dataTable.BarInset = bottom.Visible ? bottom.GripReach : 0.0f;
        if (dataTable.Visible)
            dataTable.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(center, dataTable)));
        else if (extensionViews.Visible)
            extensionViews.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(center, extensionViews)));
        else if (!LayoutTools)
            codeScroll.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(center, codeScroll)));
        else
            canvas.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(center, canvas)));
    }
}
