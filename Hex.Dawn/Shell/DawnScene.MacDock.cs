using System.Collections.Concurrent;

using Horizon.Platform;

namespace Hex.Dawn;

// The Dock icon of a Mac: a badge with the number of approvals waiting for the person, and a bounce when Hex isn't the active
// program and something needs them (an approval or a finished task once, a question until they look). The system's own
// call is the engine's IPlatformAttention, which does nothing while Hex is the active program. And the hex command, a link on
// the PATH made from the application's menu.
internal sealed partial class DawnScene
{
    private readonly MacDockState dockState = new();

    // What the thread that made the hex command's link has to say, said on the editor's thread
    private readonly ConcurrentQueue<Action> dockWork = new();

    /// <summary>Every tick, on a Mac: what waits for the person is counted and the Dock told when it changed.</summary>
    private void UpdateMacDock()
    {
        if (!OperatingSystem.IsMacOS() || options.SelfTest)
            return;

        while (dockWork.TryDequeue(out Action? work))
            work();

        if (PlatformAttention.Current is not { } dock)
            return;

        int approvals = 0, questions = 0;
        foreach (HexAgentSession session in hub.Sessions)
        {
            if (!session.Chat.HasPending)
                continue;

            foreach (HexAgentEntry entry in session.Chat.Pending)
            {
                if (entry.Kind == HexAgentEntryKind.Approval)
                    approvals++;
                else if (entry.Kind == HexAgentEntryKind.Question)
                    questions++;
            }
        }

        var told = dockState.Update(approvals, questions);
        if (told.BadgeChanged)
            dock.SetBadge(told.Badge);
        if (told.Critical)
            dock.RequestAttention(critical: true);
        else if (told.Informational)
            dock.RequestAttention(critical: false);
    }

    /// <summary>Hex, Install 'hex' Command…: the link on the PATH to this program, made off the editor's thread, the result said in a notice.</summary>
    private void InstallHexCommand()
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string? target = MacCli.Target(Hex.Core.HexBundle.Root, System.Environment.ProcessPath);
        if (target is null)
        {
            Say(Said("mac_menu.cli_failed", ("why", "this program has no path")), error: true);
            return;
        }

        _ = Task.Run(() =>
        {
            // The PATH a terminal has, which a program started from the Dock doesn't get without the login shell's
            Horizon.Core.Processes.ProcessEnvironment.WaitForLoginShell();
            MacCli.Place place = MacCli.Choose(home, System.Environment.GetEnvironmentVariable("PATH"));
            string? problem = place.NeedsAdministrator ? MacCli.LinkAsAdministrator(target, place) : MacCli.Link(target, place.Link);
            dockWork.Enqueue(() =>
            {
                if (problem is null)
                    Say(Said("mac_menu.cli_installed", ("link", place.Link)));
                else if (problem == "cancelled")
                    Say(Said("mac_menu.cli_cancelled"));
                else if (problem == "exists")
                    Say(Said("mac_menu.cli_exists", ("link", place.Link)), error: true);
                else
                    Say(Said("mac_menu.cli_failed", ("why", problem)), error: true);
            });
            Engine.WindowManager.Wake();
        });
    }
}
