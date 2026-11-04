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
    private const float SIDE_ARRIVE = 0.2f, SIDE_STAGGER = 0.025f, SIDE_DROP = 6.0f;

    // How long the code of a file switched to takes to fade up, and from how faint
    private const float CODE_ARRIVE = 0.12f, CODE_FROM = 0.84f;

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
