using Hex.Shell;

using Horizon.Core.Processes;

namespace Hex.Dusk;

// The consoles Windows passes on while Dusk is the default terminal (DuskHandoff), a tab each, the window brought to the
// front. A Dusk Windows started for one waits for it rather than starting a shell, and gives up and starts one after a
// while if it never comes (the console host went before it could pass it on).
internal sealed partial class DuskScene
{
    // How long a Dusk started for a console waits for it before it starts a shell of its own
    private const long HANDOFF_PATIENCE_MS = 15000;

    // When a Dusk started for a console began waiting for it, 0 for not waiting
    private long handoffWaitingSince;

    private void UpdateHandoffs()
    {
        if (!OperatingSystem.IsWindows())
            return;

        while (DuskHandoff.TryTake(out PseudoTerminal console, out string title))
        {
            handoffWaitingSince = 0;
            panel.AdoptTab(console);
            Say(HexText.Get("dusk_scene.handed_over", ("title", title)));
            Engine.WindowManager.BringToFront();
        }

        if (handoffWaitingSince != 0 && System.Environment.TickCount64 - handoffWaitingSince > HANDOFF_PATIENCE_MS)
        {
            handoffWaitingSince = 0;
            if (panel.Tabs.Count == 0)
                panel.NewTab(host.UsualProfile, options.StartIn);
        }
    }
}
