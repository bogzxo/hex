using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dusk;

// Dusk and Dawn in the one window, Ctrl+Alt+D (the dawn_mode command of the window plugin, the toggle on the title bar)
// swaps to the editor and back, see Hex.Shell.HexModes. The shells keep running while the editor is up, their output
// waits for the pane to be drawn again, and the tabs, splits and scrollback are as they were left.
internal sealed partial class DuskScene
{
    /// <summary>Kept when it's left for Dawn rather than thrown away, while both modes are in the window.</summary>
    public override bool Persistent => HexModes.Together;

    private Button modeToggle = null!;

    private void BuildModes()
    {
        modeToggle = top.Add(new Button(HexText.Get("dusk_modes.dawn"))
        {
            Icon = "code",
            Tooltip = HexText.Get("dusk_modes.dawn_tooltip"),
            OnPressed = SwapMode
        });

        HexModes.Leaving += mode =>
        {
            if (mode == HexMode.Dusk)
                compositor.Focus = null;
        };
    }

    /// <summary>To Dawn, on the folder the focused shell is in.</summary>
    private void SwapMode()
    {
        if (!HexModes.Swap(panel.FocusedView?.WorkingDirectory))
            Say(HexText.Get("dusk_modes.no_dawn"), error: true);
    }

    /// <summary>Back from Dawn, the keyboard in the terminal that had it.</summary>
    public override void OnReturned()
    {
        panel.FocusedView?.Focus();
        ReadEffects();
        ReadBackdrop();
        Engine.WindowManager.RequestFrames();
    }
}
