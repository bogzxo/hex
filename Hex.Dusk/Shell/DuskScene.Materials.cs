using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dusk;

// What Dusk's frame is made of, Dawn's the same way (DawnScene.Materials). The window is one surface, the title bar, the
// row of tabs, the status bar and the gaps between the panes all the one tone, and every pane is a card on it with round
// corners. With the glass on the window is a field of soft light in the theme's colours under a veil of its tone, with it
// off it's the tone alone. The panes are solid either way so what the shells write reads.
internal sealed partial class DuskScene
{
    // What Dusk's window has in common with Dawn's: the field of light under everything, the lights and sparks over
    // everything, the bars dressed for the skin, the line under the title bar (Hex.Shell's HexFrame)
    private Hex.Shell.HexFrame frame = null!;

    // How round a pane is, and the room round the panes inside the panel, under the tabs and along the window's edges
    private const float PANE_RADIUS = 12.0f;
    private static readonly UIEdges PaneRoom = new(6.0f, 0.0f, 6.0f, 6.0f);

    /// <summary>The field of light, a module of the compositor under Dusk's own, behind everything.</summary>
    private void BuildGlow() => frame = new Hex.Shell.HexFrame(compositor);

    /// <summary>The window dressed for the skin and the glass as they are now, its tone, the bars on it and the panes as cards.</summary>
    private void DressChrome()
    {
        if (compositor.Skin is not { } skin || frame is null)
            return;

        // The window and the bars along its edges, the same as Dawn's: its tone, the field of light under a veil of it on
        // glass, the title bar and the status bar the one surface. And what shape the skin makes it
        bool glassy = effectGlass;
        Hex.Shell.HexFrameShape shape = frame.Dress(skin, glassy, shared.GlassClarity, statusBar, modeToggle, top);
        panel.StripColor = null;

        // The panes cards on it, or against each other and the window's edges in a flush look (Solstice)
        panel.PaneRadius = shape.Flush ? 0.0f : PANE_RADIUS;
        panel.RoomPadding = shape.Flush ? default : PaneRoom;

        if (settingsView?.Window is { } settingsWindow)
            settingsWindow.Glass = glassy ? Hex.Shell.HexGlass.Window(popupGlass) : null;
    }
}
