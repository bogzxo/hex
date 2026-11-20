using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.UI;
using Horizon.UI.Components;

using Hex.Shell;

namespace Hex.Dawn;

// The little bits of motion Dawn adds over what the engine does by itself (windows popping in, dialogs, drawers, pages
// of tabs). A view of the side bar picked settles in from just above, and a file switched to fades its code up rather
// than slamming it in. All of it as quick as the interface animation setting says, and none at all at 0.
internal sealed partial class DawnScene
{
    // How long a view of the side bar takes to settle, a beat between its rows, and how far they drop from
    private const float SIDE_ARRIVE = 0.16f, SIDE_STAGGER = 0.012f, SIDE_DROP = 4.0f;

    // How long the code of a file switched to takes to fade up, and from how faint
    private const float CODE_ARRIVE = 0.10f, CODE_FROM = 0.96f;

    // How long the editor takes to open out as its window appears, and from how flat. The terminal's tab folding away
    // as it closes (TerminalPanel) the other way round: unfolding from the middle as it fades up, out of a cubic
    private const float OPEN_TIME = 0.16f, OPEN_FROM = 0.92f;

    // Whether this editor has opened out (once, as it first came on screen, a start or a wake, not back from Dusk), how
    // long it takes, 0 for not opening, and when it started, 0 until its first tick on screen
    private bool openedOut;
    private float openTime;
    private long openStarted;

    /// <summary>On screen from the next frame, the window shown or about to be uncovered: the editor opens out.</summary>
    protected override void OnShown()
    {
        base.OnShown();
        if (openedOut)
            return;

        openedOut = true;
        openTime = options.SelfTest ? 0.0f : MotionFor(OPEN_TIME);
        if (openTime <= 0.0f)
            return;

        // Folded from the first frame shown, so nothing is seen whole and then folded
        Opening(0.0f);
    }

    /// <summary>
    /// Helper method to draw the editor so far open, 0 folded and see-through to 1 as it is. Everything of the editor's fades with
    /// it, the documents' canvases and editors under it would show through it otherwise; the backdrop stays, the dark it
    /// opens out of.
    /// </summary>
    private void Opening(float eased)
    {
        root.VisualScale = new Vector2(1.0f, OPEN_FROM + (1.0f - OPEN_FROM) * eased);
        foreach (UICompositor faded in (UICompositor[])[stage, compositor])
        {
            foreach (UIModule module in faded.Modules)
            {
                if (module != backdrop)
                    module.Root.Opacity = eased;
            }
        }

        // The code of an editor is drawn by its own opacity, not by what it's in
        foreach (HexDocument open in documents)
            open.Editor.Opacity = eased;
    }

    /// <summary>
    /// The editor unfolding into its window, drawn only: the layout is where it'll be from the first frame and a key or a
    /// click goes where it would anyway, nothing waits on it. Off with the interface animation at 0, and in a self test,
    /// whose pictures are of the editor as it is. By the clock rather than by ticks, which catch up in a burst after the
    /// setting up the editor stood still for and had a tween over and done in one go. Every tick.
    /// </summary>
    private void UpdateOpening()
    {
        if (openTime <= 0.0f)
            return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (openStarted == 0)
            openStarted = now;

        float done = Math.Clamp((float)System.Diagnostics.Stopwatch.GetElapsedTime(openStarted, now).TotalSeconds / openTime, 0.0f, 1.0f);
        float eased = 1.0f - MathF.Pow(1.0f - done, 3.0f);
        Opening(eased);

        if (done >= 1.0f)
            openTime = 0.0f;
        else
            Engine.WindowManager.RequestFrames(0.05);
    }

    /// <summary>How long a piece of Dawn's motion lasts, scaled by the interface animation setting, 0 when it's off.</summary>
    private float MotionFor(float seconds) => HexMotion.Duration(seconds, settings.UiAnimation);

    /// <summary>The side bar's parts sliding down into place one after the other, for a view just picked.</summary>
    private void ArriveSide()
    {
        float time = MotionFor(SIDE_ARRIVE);
        if (time <= 0.0f || !left.Shown)
            return;

        int shown = 0;
        foreach (UIComponent part in left.Children)
        {
            if (!part.Visible)
                continue;

            part.SlideIn(new Vector2(0.0f, SIDE_DROP), time, MotionFor(SIDE_STAGGER * shown), Easing.OutCubic);
            shown++;
        }
    }

    /// <summary>The code of the file just switched to fading up from faint.</summary>
    private void ArriveCode(HexDocument shown)
    {
        float time = MotionFor(CODE_ARRIVE);
        if (time <= 0.0f || !(shown.IsText || shown.View == HexView.Code))
            return;

        shown.Editor.Opacity = CODE_FROM;
        shown.Editor.TweenOpacity(1.0f, time).SetEasing(Easing.OutCubic);
    }
}
