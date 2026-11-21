using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// Dusk and Dawn in the one window, Ctrl+Alt+Shift+D (the dawn_mode command of the window plugin, the toggle on the title bar)
// swaps to the editor and back, see Hex.Shell.HexModes. The shells keep running while the editor is up, their output
// waits for the pane to be drawn again, and the tabs, splits and scrollback are as they were left.
internal sealed partial class DuskScene
{
    /// <summary>Kept when it's left for Dawn rather than thrown away, while both modes are in the window.</summary>
    public override bool Persistent => HexModes.Together;

    private Button modeToggle = null!;

    // How far the terminal slides in from as it comes back, Dawn's faces' distance
    private const float ARRIVE_SHIFT = 16.0f;
    private UIComponent? focusBeforeMode;

    private void BuildModes()
    {
        modeToggle = HexModeSwitch.Add(top, HexFace.Dusk, face =>
        {
            // Noon is the editor's, the editor's brought back showing it
            HexModes.FaceWanted = face;
            SwapMode();
        });
        // What Dusk's frames are painted as, for a test watching them (Horizon's FrameTrace)
        compositor.TraceTag = HexFace.Dusk;

        HexModes.Leaving += mode =>
        {
            if (mode == HexMode.Dusk)
            {
                focusBeforeMode = compositor.Focus;
                compositor.Focus = null;
            }
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
        if (!HexModeSwitch.RestoreFocus(compositor, focusBeforeMode))
            panel.FocusedView?.Focus();
        ReadEffects();
        ReadBackdrop();

        // Under the cover of the editor's last frame, the terminal slides the last few pixels in from the right, where its
        // button is on the switch
        if (Hex.Shell.HexModes.ArriveTime > 0.0f)
        {
            panel.VisualOffset = new System.Numerics.Vector2(ARRIVE_SHIFT, 0.0f);
            panel.TweenOffset(System.Numerics.Vector2.Zero, Hex.Shell.HexModes.ArriveTime).SetEasing(Horizon.Core.Tweening.Easing.OutCubic);
        }

        // The editor was made bigger or smaller meanwhile, the terminal follows so the swap doesn't jump
        if (Hex.Shell.HexModes.InterfaceSize is { } shared && shared != compositor.Scale)
            SetInterfaceSize(shared);
        Engine.WindowManager.RequestFrames();
    }
}
