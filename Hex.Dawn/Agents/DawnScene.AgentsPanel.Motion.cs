using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// What moves on the Agents page, so it feels alive while an agent works and stays still when it doesn't. A row slides up
// into place as it comes, a tool that's running breathes, one that's done gives a little pop, three dots bounce while the
// agent's thinking and hasn't said anything yet, and an agent that's working pulses in the list. All of it through the
// components' own tweens and as long as the settings' UI animation says, none of it at all with that at 0.
internal sealed partial class DawnScene
{
    // How long the page's motion takes at the usual speed, in seconds
    private const float AGENTS_SLIDE = 0.32f, AGENTS_POP = 0.28f, AGENTS_BREATHE = 0.7f, AGENTS_BOUNCE = 0.38f;

    /// <summary>How long a piece of motion lasts, scaled by the UI animation setting, 0 when it's off.</summary>
    private float AgentsMotion(float seconds) => settings.UiAnimation <= 0.0f ? 0.0f : seconds * settings.UiAnimation;

    /// <summary>A row sliding up into its place and fading in, the delay for a few coming at once one after another.</summary>
    private void SlideRowIn(UIComponent row, float delay = 0.0f, float fromX = 0.0f)
    {
        float time = AgentsMotion(AGENTS_SLIDE);
        if (time <= 0.0f)
            return;
        row.SlideIn(new Vector2(fromX, -14.0f), time, AgentsMotion(delay), Easing.OutCubic);
    }

    /// <summary>A row that just changed (a tool that finished) swelling a touch and settling back.</summary>
    private void PopRow(UIComponent row)
    {
        float time = AgentsMotion(AGENTS_POP);
        if (time <= 0.0f)
            return;
        row.VisualScale = new Vector2(0.97f, 0.97f);
        row.TweenScale(1.0f, time).SetEasing(Easing.OutBack);
    }

    /// <summary>Something that's going on breathing, its opacity going down and back up for as long as it's there.</summary>
    private void Breathe(UIComponent part, float least = 0.45f)
    {
        float time = AgentsMotion(AGENTS_BREATHE);
        if (time <= 0.0f)
            return;
        part.TweenOpacity(least, time).SetEasing(Easing.InOutSine).SetLoops(-1, LoopMode.PingPong);
    }

    /// <summary>A button coming into sight with a pop, Stop as an agent starts.</summary>
    private void PopIn(UIComponent part)
    {
        float time = AgentsMotion(AGENTS_POP);
        if (time > 0.0f)
            part.PopIn(time);
    }

    /// <summary>
    /// The three dots that bounce while the agent's working and hasn't said anything new, made once and kept at the end
    /// of the rows, each dot a beat behind the one before.
    /// </summary>
    private Panel MakeTypingDots()
    {
        var row = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Padding = new UIEdges(8, 6, 0, 4) };
        var dots = row.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 5, Anchor = Origin.Left, Pivot = Origin.Left });
        for (int i = 0; i < 3; i++)
            dots.Add(new Panel { Size = new Vector2(7, 7), Radius = 3.5f, Color = new Vector4(1, 1, 1, 0.5f) });
        return row;
    }

    /// <summary>The dots set bouncing in the theme's accent, or still when motion's off.</summary>
    private void BounceDots(Panel row)
    {
        Vector4 accent = Skin?.Palette.Accent ?? new Vector4(0.4f, 0.6f, 1.0f, 1.0f);
        float time = AgentsMotion(AGENTS_BOUNCE);
        int i = 0;
        foreach (UIComponent dot in row.Children[0].Children)
        {
            ((Panel)dot).Color = accent with { W = 0.85f };
            dot.Tweens.KillAll();
            dot.VisualOffset = Vector2.Zero;
            if (time > 0.0f)
                dot.TweenOffset(new Vector2(0, 5), time).SetEasing(Easing.InOutSine).SetLoops(-1, LoopMode.PingPong).SetDelay(i * time / 3.0f);
            i++;
        }
    }
}
