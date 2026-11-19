using Hex.Shell;

using Horizon.Core.Threading;

namespace Hex.Dawn;

// The horizon in Dawn, see HexHorizon. It runs while a task is going in a terminal (a build, the tests, a run) or work
// in the background is (a check for updates, an update coming down, a language server being installed, the catalogue of
// extensions read), and goes the error's red for a moment when either ends badly. Whatever says how far along it is, as
// a share of the whole, fills the line that far: every percentage Hex reports is on this one line, where a card in the
// corner each had a bar of its own. The cards are still there for what the line can't say, what the work is called,
// what it's doing, stopping it and what it wrote.
internal sealed partial class DawnScene
{
    // How long the line stays red after a task that went wrong, in milliseconds
    private const long HORIZON_FAILED_MS = 8000;

    // Until when the line says the last task failed, in the ticks of Environment.TickCount64
    private long horizonFailedUntil;

    // The failure the status bar was last told of, by when its red ends
    private long horizonFailureSaid;

    // Made once, they're asked every tick. Work that goes without saying so isn't on the line, and of the rest the
    // newest that knows how far it got is the one the line follows
    private static readonly Func<WorkItem, bool> seenWork = item => !item.Key.StartsWith(QUIET_BACKGROUND, StringComparison.Ordinal);
    private static readonly Func<WorkItem, bool> measuredWork = item => seenWork(item) && item.Progress is not null;

    /// <summary>The line for the tests, whether it's there and how full.</summary>
    internal HexHorizon? HorizonLine => frame.Horizon;

    /// <summary>A task ended, the line goes red for a moment when it ended badly.</summary>
    private void NoteTaskEnded(int code)
    {
        if (code != 0)
            NoteWorkFailed();
    }

    /// <summary>Something that was going went wrong, a task or work in the background: the line is red for a moment.</summary>
    private void NoteWorkFailed() => Volatile.Write(ref horizonFailedUntil, System.Environment.TickCount64 + HORIZON_FAILED_MS);

    // In Agentic the line says the agents, how many are working and until when it's red for one that failed, put right
    // when the chats move rather than every tick
    private int horizonAgentsWorking, horizonAgentsFailing;
    private long horizonAgentFailedUntil;

    /// <summary>The agents as Agentic's line says them, counted again as the chats move.</summary>
    private void CountHorizonAgents()
    {
        int working = 0, failing = 0;
        foreach (HexAgentChat chat in agentChats)
        {
            if (chat.Running)
                working++;
            else if (chat.Failed)
                failing++;
        }

        // One more that ended badly turns the line red for a moment, the way a task that did does in Dawn
        if (failing > horizonAgentsFailing)
            Volatile.Write(ref horizonAgentFailedUntil, System.Environment.TickCount64 + HORIZON_FAILED_MS);
        horizonAgentsWorking = working;
        horizonAgentsFailing = failing;
    }

    /// <summary>
    /// Every tick, the line set for what's going. It sets nothing when nothing changed. In Agentic it's the agents, running
    /// while any works, red when one fails, Dawn's builds and work having the status bar there.
    /// </summary>
    private void UpdateHorizon()
    {
        if (compositor.Skin is not { } skin || top.Bounds.IsEmpty)
            return;

        if (Face == HexFace.Noon)
        {
            bool working = horizonAgentsWorking > 0;
            if (working)
                horizonAgentFailedUntil = 0;
            HexHorizonState said = working ? HexHorizonState.Running
                : System.Environment.TickCount64 < Volatile.Read(ref horizonAgentFailedUntil) ? HexHorizonState.Failed : HexHorizonState.Idle;
            frame.UpdateHorizon(chrome.Root, skin, top.Bounds.Height, chrome.Root.Bounds.Width, said, null);
            return;
        }

        bool tasks = taskWatches.Count > 0;
        bool running = tasks || work.CountActive(seenWork) > 0;

        // Something new going takes the red of the last one away
        if (running)
            horizonFailedUntil = 0;

        HexHorizonState state = running ? HexHorizonState.Running
            : System.Environment.TickCount64 < Volatile.Read(ref horizonFailedUntil) ? HexHorizonState.Failed : HexHorizonState.Idle;

        // Red is said in words too, once a failure, on the status bar where it stays until it's pressed (which opens the
        // output with why). The line going red was all there was, gone in eight seconds and nothing for anybody who can't
        // tell red from the rest
        long failedUntil = Volatile.Read(ref horizonFailedUntil);
        if (state == HexHorizonState.Failed && failedUntil != horizonFailureSaid)
        {
            horizonFailureSaid = failedUntil;
            Say(Said("status_bar.work_failed"), error: true);
        }

        // As far as whatever knows how far it is has got, a task in a terminal never does. With none that knows the line
        // is full and fainter, something is going and that's all that can be said
        float? progress = running ? work.NewestActive(measuredWork)?.Progress : null;
        frame.UpdateHorizon(chrome.Root, skin, top.Bounds.Height, chrome.Root.Bounds.Width, state, progress);
    }
}
