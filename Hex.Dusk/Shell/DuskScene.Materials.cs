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
    // The module under Dusk's own that the field of light is in, and the field
    private UIModule glowModule = null!;
    private GlowField glow = null!;

    // How round a pane is, and the room round the panes inside the panel, under the tabs and along the window's edges
    private const float PANE_RADIUS = 12.0f;
    private static readonly UIEdges PaneRoom = new(6.0f, 0.0f, 6.0f, 6.0f);

    // How much of the window's tone is laid over the field of light on glass, by the glass's clarity the way Dawn's is
    private float veil = Hex.Shell.HexGlass.Veil(Hex.Shell.HexGlass.CLARITY);

    /// <summary>The field of light, a module of the compositor under Dusk's own, behind everything.</summary>
    private void BuildGlow()
    {
        glowModule = compositor.CreateModule();
        glowModule.ShowInLayoutDebugger = false;
        glowModule.Interactive = false;
        glow = glowModule.Root.Add(new GlowField { Fill = UIFill.Both });
    }

    /// <summary>The window dressed for the skin and the glass as they are now, its tone, the bars on it and the panes as cards.</summary>
    private void DressChrome()
    {
        if (compositor.Skin is not { } skin || glow is null)
            return;

        bool glassy = effectGlass;
        Vector4 panelColour = skin.PanelColor with { W = 1.0f };
        Vector4 window = new(panelColour.X * 0.5f, panelColour.Y * 0.5f, panelColour.Z * 0.5f, 1.0f);

        glow.UseSkin(skin);
        glow.Veil = glassy ? window with { W = veil } : window;

        // The bars are the window and nothing of their own
        top.Color = statusBar.Color = window with { W = 0.0f };
        top.Glass = statusBar.Glass = null;
        statusBar.Hairline = false;
        panel.StripColor = null;

        // The panes cards on it
        panel.PaneRadius = PANE_RADIUS;
        panel.RoomPadding = PaneRoom;

        if (settingsView?.Window is { } settingsWindow)
            settingsWindow.Glass = glassy ? Hex.Shell.HexGlass.Window(popupGlass) : null;
    }
}
