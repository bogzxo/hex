using Hex.Shell;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The tabs kept from one run to the next (sessions.hor next to the settings, TerminalSessions writes and reads it),
// written a few seconds after anything about the tabs changes and as Dusk closes, put back as it starts when
// restore_sessions says so.
internal sealed partial class DuskScene
{
    private const float SESSION_EVERY = 3.0f;
    private float sessionDue = SESSION_EVERY;
    private TerminalSessions.Snapshot? savedSessionState;

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

    // Compare what the writer keeps, including names and folders even when the splits haven't moved.
    private void UpdateSession(float dt)
    {
        sessionDue -= dt;
        if (sessionDue > 0.0f)
            return;
        sessionDue = SESSION_EVERY;
        SaveSession();
    }

    private void SaveSession()
    {
        if (panel.Tabs.Count == 0)
            return;

        try
        {
            if (savedSessionState is not null && TerminalSessions.Matches(panel, savedSessionState))
                return;

            string session = TerminalSessions.Write(panel);
            TerminalSessions.Snapshot state = TerminalSessions.Capture(panel);
            File.WriteAllText(SessionFile, session);
            savedSessionState = state;
        }
        catch (Exception e)
        {
            Say(HexText.Get("dusk_session.save_failed", ("message", e.Message)), error: true);
        }
    }
}
