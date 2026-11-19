using Hex.Shell;

namespace Hex.Dawn;

// The editor put away whole as Hex goes to sleep between uses (the persistent_process setting, see DawnResident): what it
// kept, written down, and everything it had going stopped, the processes it started, the watchers and the timers, so a
// process with no editor in it does nothing at all and a new editor made in it later meets nothing of the old one's
internal sealed partial class DawnScene
{
    // The frame work the editor asked of the window manager, the pictures of closed images freed and a notebook's
    // outputs made, kept to be taken off again
    private Action<float>? picturesFreed, outputPicturesMade;

    /// <summary>Puts the editor away and Hex to sleep. On the editor's thread, once closing was answered.</summary>
    private void Sleep()
    {
        Unload();
        DawnResident.Sleep(Engine);
    }

    /// <summary>
    /// Stops everything the editor started that outlives it otherwise: its agents, language servers, kernels, shells,
    /// debugger, MCP server, watchers and timers, and what it left with things that are the process's.
    /// </summary>
    private void Unload()
    {
        // What closing keeps, said once here as the scene's disposal says it again
        FlushZoomSettings(report: false);
        KeepChats();
        chatsKept?.Flush();
        harness?.SaveCache();
        explorer.FlushKept();

        Quietly(StopMcp);
        foreach (AgentRun run in agentRuns.ToList())
            Quietly(() => KillAgentRun(run));
        HexAgentProcess.Woken = null;

        Quietly(debugger.Quit);
        foreach (HexNotebook notebook in notebooks.Values.ToList())
            Quietly(() => StopKernel(notebook));
        Quietly(Horizon.UI.Scripting.LanguageServers.StopAll);

        Quietly(terminals.Dispose);
        Quietly(terminalPanel.Dispose);

        Quietly(folders.Dispose);
        Quietly(disk.Dispose);
        Quietly(StopWatchingSettings);
        Quietly(StopWatchingWorkspaceSettings);
        Quietly(extensions.StopWatching);
        StopRemote();

        // What this editor left with the modes, which would call it and keep it
        if (leavingMode is not null)
            HexModes.Leaving -= leavingMode;
        HexModes.Forget(HexMode.Dawn);
        HexModes.Forget(HexMode.Dusk);
        work.Woken = null;

        // And what it left with the engine and the process, which outlive it
        LetGo();
        properties.Forget();
        HexHost.Report = null;
        HexSearch.Excluded = static () => HexExcludes.Usual;
        if (picturesFreed is not null)
            Engine.WindowManager.Exclusive -= picturesFreed;
        if (outputPicturesMade is not null)
            Engine.WindowManager.Exclusive -= outputPicturesMade;
        if (renderPlanHeard is not null)
            Engine.WindowManager.Scaling.PlanChanged -= renderPlanHeard;
        if (harnessExit is not null)
            AppDomain.CurrentDomain.ProcessExit -= harnessExit;
        AppDomain.CurrentDomain.ProcessExit -= ForgetMcpFile;

        static void Quietly(Action stop)
        {
            try
            {
                stop();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Hex] putting the editor away, {e.Message}");
            }
        }
    }
}
