using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// What the editor's frame is made of. The window is one surface, the title bar, the activity bar, the status bar and
// the gaps all the one tone, and on it float three cards with round corners, the side bar, the middle and the right
// side. With the glass on (the Fluid and Frosted looks) the window is a field of soft light in the theme's own colours
// under a veil, and the sides are glass over it, the middle solid so what's read there reads. With it off (Flat) the
// window is a plain dark and the cards are solid. Every look has the same shapes. Strips of three different darks with
// square corners against round ones was what made it look put together out of parts. A square theme (Visual Studio's) is
// the one other shape, every card flush and square on a window of the cards' own tone.
internal sealed partial class DawnScene
{
    // The module under the editor's that the field of light is in, and the field
    private UIModule glowModule = null!;
    private GlowField glow = null!;

    // How far in from its column a card is drawn (twice that is the gap between two), and how round it is
    private const float CARD_INSET = 4.0f, CARD_RADIUS = 12.0f;

    // How round the dark well the layouts are shown in is, and the panes of the terminal, inside the middle card
    private const float WELL_RADIUS = 8.0f, TERMINAL_RADIUS = 8.0f;

    // How far the shadow under something popped up over the editor reaches
    private const float POPUP_LIFT = 18.0f;

    /// <summary>
    /// Whether a skin is square, a theme with no round to its corners (Visual Studio's). Its frame is drawn the way that
    /// editor's is, the cards flush against each other with no gaps and no round, and the window their own tone, the
    /// hairlines round them all that tells them apart.
    /// </summary>
    internal static bool Square(Horizon.UI.Skinning.UISkin skin) => skin.CornerRadius <= 0.0f;

    /// <summary>The field of light, a module of the editor's compositor under the editor's own, with a hole where the canvas is.</summary>
    private void BuildGlow()
    {
        glowModule = compositor.CreateModule();
        glowModule.ShowInLayoutDebugger = false;
        glowModule.Interactive = false;
        glow = glowModule.Root.Add(new GlowField { Fill = UIFill.Both, Hole = () => CanvasHole(glowModule) });
    }

    /// <summary>
    /// Where the canvas is in a module of the editor's compositor, the layouts being designed are seen through there and
    /// nothing of the editor's is painted over them. Null while it's away (the code is up).
    /// </summary>
    private UIRect? CanvasHole(UIModule into)
    {
        if (!canvas.Visible || canvas.Bounds.IsEmpty)
            return null;

        Vector2 min = into.ToLocal(chrome.ToWorld(canvas.Bounds.Min)), max = into.ToLocal(chrome.ToWorld(canvas.Bounds.Max));
        return new UIRect(Vector2.Min(min, max), Vector2.Max(min, max));
    }

    /// <summary>
    /// The frame dressed for the skin and the glass as they are now, after either changed. The window's tone, the cards on
    /// it, glass or solid, and what's inside them left to the cards.
    /// </summary>
    private void DressChrome()
    {
        if (compositor.Skin is not { } skin || glow is null || statusBar is null)
            return;

        bool glassy = Glassy;
        Vector4 card = skin.PanelColor with { W = 1.0f };
        bool square = Square(skin);
        float inset = square ? 0.0f : CARD_INSET, radius = square ? 0.0f : CARD_RADIUS;
        Vector4 window = square ? card : new(card.X * 0.5f, card.Y * 0.5f, card.Z * 0.5f, 1.0f);
        Vector4 none = card with { W = 0.0f };

        // The window, the field of light under a veil of its tone on glass, the tone alone without
        glow.Visible = true;
        glow.UseSkin(skin);
        // How much of the window's tone is over the field of light, less for clearer glass (HexGlass.Veil)
        glow.Veil = glassy ? window with { W = HexGlass.Veil(settings.GlassClarity) } : window;

        // The bars along the edges are the window, nothing of their own, and the status bar has no line between it and
        // what's over it, they're one surface
        activity.Color = top.Color = statusBar.Color = none;
        top.Glass = activity.Glass = statusBar.Glass = null;
        statusBar.Hairline = false;

        // The cards. The sides on glass over the window, or solid, the middle solid always with the canvas a window
        // through it. A hairline of the skin's edge round each, the way glass has an edge
        foreach (Panel side in (ReadOnlySpan<Panel>)[left, right])
        {
            side.Inset = inset;
            side.Radius = radius;
            side.Color = card;
            side.Glass = glassy ? SideGlass : null;
            side.Outline = skin.BorderColor;
        }

        center.Inset = inset;
        center.Radius = radius;
        center.Color = card;
        center.Hole = () => CanvasHole(chrome);
        center.Outline = skin.BorderColor;
        center.Glass = null;
        bottom.Glass = null;

        // What's inside the cards is the card, the lists have no boxes of their own and the code is the card's colour
        treeScroll.Color = layersScroll.Color = inspectorScroll.Color = none;
        running.ValuesTree.Color = sideTree.Color = none;
        codeScroll.Color = card;
        foreach (HexDocument open in documents)
            open.Editor.Color = card;
        if (welcome is not null)
            welcome.Color = card;

        // The terminals of the panel under the editor round cornered like Dusk's, inside the card
        if (terminalPanel is not null)
            terminalPanel.PaneRadius = square ? 0.0f : TERMINAL_RADIUS;

        // What shows round the canvas's well where its corners are round is the card, and the well is the skin's field, the
        // same hue a shade under it
        Engine.Graphics.ClearColor = card;
        if (backdrop.Root.Children.FirstOrDefault() is Panel well)
        {
            well.Color = skin.FieldColor with { W = 1.0f };
            well.Radius = square ? 0.0f : WELL_RADIUS;
        }

        // The find bar and the debug bar are popups over the code like the palette and the menus, the same stuff and the
        // same round
        foreach (Panel? bar in (Panel?[])[codeEditing?.findBar, debugBar])
        {
            if (bar is null)
                continue;
            bar.Color = skin.Palette.Raised;
            bar.Radius = skin.PopupRadius;
            bar.Outline = skin.BorderColor;
            bar.Glass = glassy ? PopupGlass : null;
            bar.Lift = POPUP_LIFT;
        }

        // The windows that are up, the settings, the keys, a page of help
        foreach (WindowPanel up in chrome.Root.Children.OfType<WindowPanel>())
            DressWindow(up);
        treeDirty = inspectorDirty = true;
    }

    /// <summary>
    /// A little window of the editor's dressed the one way they all are, the raised tone every popup is made of on the
    /// windows' glass, or solid without. The keys and the help were the card's navy and the settings were glass, three
    /// windows of three different stuffs.
    /// </summary>
    internal void DressWindow(WindowPanel window)
    {
        window.Color = compositor.Skin?.Palette.Raised;
        window.Glass = Glassy ? Hex.Shell.HexGlass.Window(PopupGlass) : null;
    }
}
