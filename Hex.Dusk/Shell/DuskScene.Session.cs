using Hex.Shell;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The tabs kept from one run to the next (sessions.hor next to the settings, TerminalSessions writes and reads it),
// written a few seconds after anything about the tabs changes and as Dusk closes, put back as it starts when
// restore_sessions says so.
internal sealed partial class DuskScene
{
    private const float SESSION_EVERY = 3.0f;
    private float sessionDue = -1.0f;
    private int sessionShape;

    private string SessionFile => TerminalFiles.Sessions(options.SelfTest ? null : AppContext.BaseDirectory);

    /// <summary>The tabs of last time opened, false when there were none to open.</summary>
    private bool RestoreSession()
    {
        try
        {
            if (!File.Exists(SessionFile) || TerminalSessions.Read(File.ReadAllText(SessionFile)) is not { } session)
                return false;

            return TerminalSessions.Restore(panel, session, host.ProfileCalled);
        }
        catch (Exception e)
        {
            Say(HexText.Get("dusk_session.restore_failed", ("message", e.Message)), error: true);
            return false;
        }
    }

    /// <summary>Every tick, the session written a while after the tabs or their splits changed.</summary>
    private void UpdateSession(float dt)
    {
        // A cheap fingerprint of the tabs, their panes and which is open, nothing allocated for it
        int shape = panel.Tabs.Count * 7919 + panel.CurrentIndex;
        for (int i = 0; i < panel.Tabs.Count; i++)
            shape = shape * 31 + (panel.Tabs[i].Tree.Root is { } root ? Shape(root) : 0);

        if (shape != sessionShape)
        {
            sessionShape = shape;
            sessionDue = SESSION_EVERY;
        }

        if (sessionDue > 0.0f && (sessionDue -= dt) <= 0.0f)
            SaveSession();
    }

    private static int Shape(SplitNode<TerminalView> node) =>
        node.IsLeaf ? 1 : 17 + (int)(node.Ratio * 100) + 3 * Shape(node.First!) + 5 * Shape(node.Second!);

    private void SaveSession()
    {
        if (panel.Tabs.Count == 0)
            return;

        try
        {
            File.WriteAllText(SessionFile, TerminalSessions.Write(panel));
        }
        catch (Exception e)
        {
            Say(HexText.Get("dusk_session.save_failed", ("message", e.Message)), error: true);
        }
    }
}
