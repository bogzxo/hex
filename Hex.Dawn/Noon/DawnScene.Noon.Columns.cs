using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// How wide Agentic's side columns are, the fleet down the left and Details down the right. Each is dragged by a handle on
// the line between it and the chat (a HexSplitter, the one Dawn's sides have), between its least and its most, kept in
// the settings beside Dawn's sides and put back to Agentic's own width by a double click on the line. The fleet dragged on
// past its least is put away as it's let go of, the way Dawn's side bar is. A window too narrow for both at their widths
// and a chat worth reading takes the room from both in step, and they come back to their widths as it widens.
internal sealed partial class DawnScene
{
    // The least and the most the fleet and Details are dragged to, and the least the chat between them is left
    private const float MIN_NOON_FLEET = 220.0f, MAX_NOON_FLEET = 560.0f;
    private const float MIN_NOON_CONTEXT = 240.0f, MAX_NOON_CONTEXT = 600.0f;
    private const float NOON_MIDDLE_LEAST = 460.0f;

    // How wide Details is laid out this frame, and how wide it was when its rows were last made
    private float noonContextWide = NOON_CONTEXT, noonContextDrawnWide = -1.0f;

    // The handles on the two lines, the fleet's and Details'
    private Hex.Shell.HexSplitter? noonFleetSplitter, noonContextSplitter;

    /// <summary>The handles of Agentic's columns for the tests, the fleet's and Details'.</summary>
    internal (Hex.Shell.HexSplitter? Fleet, Hex.Shell.HexSplitter? Context) NoonSplitters => (noonFleetSplitter, noonContextSplitter);

    private static float PreferredNoon(float saved, float usual, float least, float most) => Math.Clamp(saved > 0.0f ? saved : usual, least, most);

    /// <summary>
    /// The two columns made to be dragged by their edges facing the chat. The fleet's drag goes on past its least to half
    /// of it, let go of there it's put away. What they're dragged to is kept only while it's wide enough to keep.
    /// </summary>
    private void GripNoon()
    {
        noonFleet.MinSize = new Vector2(MIN_NOON_FLEET * 0.5f, 0.0f);
        noonFleet.MaxSize = new Vector2(MAX_NOON_FLEET, 0.0f);
        noonFleet.OnResized = size =>
        {
            if (size.X >= MIN_NOON_FLEET)
                settings.NoonFleetWidth = MathF.Floor(size.X);
        };
        noonFleet.OnResizeEnded = size =>
        {
            if (size.X < MIN_NOON_FLEET)
                ShowFleet(false);
            SaveSettings();
        };

        noonContext.MinSize = new Vector2(MIN_NOON_CONTEXT, 0.0f);
        noonContext.MaxSize = new Vector2(MAX_NOON_CONTEXT, 0.0f);
        noonContext.OnResized = size => settings.NoonContextWidth = MathF.Floor(size.X);
        noonContext.OnResizeEnded = _ => SaveSettings();

        // Held by the handles on the lines alone, as Dawn's sides are, never by a grip of their own as well
        noonFleet.Resizable = noonContext.Resizable = UIResize.None;
    }

    /// <summary>
    /// How wide the fleet and Details are on a window this wide, each its kept width unless the window can't give the chat
    /// its least beside them, when the room over their least is shared out between them in step. One being dragged is as
    /// wide as the drag has it, and neither can be dragged so wide the chat's left under its least.
    /// </summary>
    private (float Fleet, float Context) NoonWidths(float width, bool fleet, bool context)
    {
        float fleetWidth = fleet ? PreferredNoon(settings.NoonFleetWidth, NOON_FLEET, MIN_NOON_FLEET, MAX_NOON_FLEET) : 0.0f;
        float contextWidth = context ? PreferredNoon(settings.NoonContextWidth, NOON_CONTEXT, MIN_NOON_CONTEXT, MAX_NOON_CONTEXT) : 0.0f;
        float fleetLeast = fleet ? MIN_NOON_FLEET : 0.0f, contextLeast = context ? MIN_NOON_CONTEXT : 0.0f;
        float extra = fleetWidth - fleetLeast + (contextWidth - contextLeast);
        float budget = width - NOON_MIDDLE_LEAST - fleetLeast - contextLeast;
        if (extra > 0.0f && budget < extra)
        {
            float share = Math.Clamp(budget / extra, 0.0f, 1.0f);
            fleetWidth = MathF.Floor(fleetLeast + (fleetWidth - fleetLeast) * share);
            contextWidth = MathF.Floor(contextLeast + (contextWidth - contextLeast) * share);
        }

        if (fleet && noonFleet.IsResizing)
            fleetWidth = noonFleet.Size.X;
        if (context && noonContext.IsResizing)
            contextWidth = noonContext.Size.X;

        // The most each is dragged to, the chat keeping its least beside the other
        float fleetMost = Math.Clamp(width - contextWidth - NOON_MIDDLE_LEAST, MIN_NOON_FLEET, MAX_NOON_FLEET);
        float contextMost = Math.Clamp(width - fleetWidth - NOON_MIDDLE_LEAST, MIN_NOON_CONTEXT, MAX_NOON_CONTEXT);
        if (noonFleet.MaxSize.X != fleetMost)
            noonFleet.MaxSize = new Vector2(fleetMost, 0.0f);
        if (noonContext.MaxSize.X != contextMost)
            noonContext.MaxSize = new Vector2(contextMost, 0.0f);
        return (fleetWidth, contextWidth);
    }

    /// <summary>The fleet back at Agentic's own width, a double click on its line.</summary>
    private void ResetNoonFleet()
    {
        settings.NoonFleetWidth = 0.0f;
        noonFleetDrawn = NOT_DRAWN;
        SaveSettings();
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>Details back at Agentic's own width, a double click on its line.</summary>
    private void ResetNoonContext()
    {
        settings.NoonContextWidth = 0.0f;
        SaveSettings();
        Engine.WindowManager.RequestFrames();
    }

    /// <summary>
    /// Every tick, the handles of Agentic's columns laid on the lines between them and the chat, while Agentic is up and
    /// the column is beside the chat. None for the fleet opened over a narrow window in the chat's place.
    /// </summary>
    private void PlaceNoonGrips(UIRect window, Vector4 accent, bool skinned)
    {
        if (noonView is null)
            return;

        if (noonFleetSplitter is null)
        {
            noonFleetSplitter = chrome.Root.Add(new Hex.Shell.HexSplitter { OnReset = ResetNoonFleet });
            noonContextSplitter = chrome.Root.Add(new Hex.Shell.HexSplitter { OnReset = ResetNoonContext });
        }

        bool ready = skinned && !window.IsEmpty && Face == HexFace.Noon && noonView.Visible;
        bool fleet = ready && noonFleet.Visible && noonCenter.Visible && !noonFleet.Bounds.IsEmpty;
        bool context = ready && noonContext.Visible && !noonContext.Bounds.IsEmpty;
        noonFleetSplitter.Place(fleet ? noonFleet : null, UIResize.Right,
            new Vector2(noonFleet.Bounds.Max.X - window.Min.X, window.Max.Y - noonFleet.Bounds.Max.Y), GRIP_HOLD, noonFleet.Bounds.Height, accent);
        noonContextSplitter!.Place(context ? noonContext : null, UIResize.Left,
            new Vector2(noonContext.Bounds.Min.X - window.Min.X, window.Max.Y - noonContext.Bounds.Max.Y), GRIP_HOLD, noonContext.Bounds.Height, accent);
    }
}
