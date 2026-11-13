using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Shell;

/// <summary>What the line under the title bar is saying.</summary>
internal enum HexHorizonState
{
    // Nothing going, the line is a hairline of the skin's edge
    Idle,

    // Something is running, the line fills in the accent as far as it got, all the way and fainter when that can't be said
    Running,

    // What ran last went wrong, the line is the skin's error red for a moment
    Failed
}

/// <summary>
/// The horizon, the one line across a flush window (the Solstice looks) under its title bar. It's where a build, the
/// tests or work in the background show that they're going and how they ended, in the accent of the mode the window is
/// in, so that's read without looking down at the status bar. Two panels laid over the window, the line and what fills
/// it. Nothing of it moves by itself: it is set again only when what it says changed, so an idle Hex draws nothing for it.
/// A look that isn't flush has no horizon, its window is cards on one surface with no line to run along.
/// </summary>
internal sealed class HexHorizon
{
    /// <summary>How thick the line is.</summary>
    public const float HEIGHT = 2.0f;

    // How much of the accent a run that can't say how far it got is drawn in, against the full accent of one that can
    private const float UNKNOWN = 0.55f;

    private readonly Panel line, fill;

    // What it was last set to, so a tick that changes nothing sets nothing
    private float top = -1.0f, width = -1.0f, share = -1.0f;
    private Vector4 lineColour, fillColour;
    private bool shown;

    public HexHorizon(UIComponent over)
    {
        line = over.Add(new Panel { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, ZOffset = 4, Visible = false });
        fill = over.Add(new Panel { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, ZOffset = 5, Visible = false });
    }

    /// <summary>How much of the line is filled, 0 idle and 1 all of it, for the tests.</summary>
    public float Share => shown ? MathF.Max(share, 0.0f) : 0.0f;

    /// <summary>Whether the line is there at all, which it is in a flush look.</summary>
    public bool Shown => shown;

    /// <summary>The colour of what fills the line now.</summary>
    public Vector4 FillColour => fillColour;

    /// <summary>
    /// Sets the line for the skin and what's going. <paramref name="under"/> is how far down from the top of the window
    /// the title bar ends, <paramref name="across"/> how wide the window is, <paramref name="progress"/> how far a run got
    /// from 0 to 1, null when it can't say.
    /// </summary>
    public void Update(UISkin skin, float under, float across, HexHorizonState state, float? progress)
    {
        bool wanted = HexLooks.Flush(skin) && under > 0.0f && across > 0.0f;
        if (wanted != shown)
        {
            shown = wanted;
            line.Visible = wanted;
            share = -1.0f;
        }

        if (!wanted)
        {
            if (fill.Visible)
                fill.Visible = false;
            return;
        }

        float filled = state switch
        {
            HexHorizonState.Running => Math.Clamp(progress ?? 1.0f, 0.02f, 1.0f),
            HexHorizonState.Failed => 1.0f,
            _ => 0.0f
        };
        Vector4 edge = skin.BorderColor;
        Vector4 colour = state switch
        {
            HexHorizonState.Failed => skin.Palette.Error,
            HexHorizonState.Running when progress is null => skin.Palette.Accent with { W = UNKNOWN },
            _ => skin.Palette.Accent
        };

        if (under != top || across != width)
        {
            top = under;
            width = across;
            line.Position = new Vector2(0.0f, -under);
            line.Size = new Vector2(across, HEIGHT);
            fill.Position = line.Position;
            share = -1.0f;
        }

        if (edge != lineColour)
            line.Color = lineColour = edge;

        if (filled != share)
        {
            share = filled;
            fill.Visible = filled > 0.0f;
            fill.Size = new Vector2(across * filled, HEIGHT);
        }

        if (filled > 0.0f && colour != fillColour)
            fill.Color = fillColour = colour;
    }
}
