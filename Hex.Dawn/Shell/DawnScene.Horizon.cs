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

    /// <summary>Every tick, the line set for what's going. It sets nothing when nothing changed.</summary>
    private void UpdateHorizon()
    {
        if (compositor.Skin is not { } skin || top.Bounds.IsEmpty)
            return;

        bool tasks = taskWatches.Count > 0;
        bool running = tasks || work.CountActive(seenWork) > 0;

        // Something new going takes the red of the last one away
        if (running)
            horizonFailedUntil = 0;

        HexHorizonState state = running ? HexHorizonState.Running
            : System.Environment.TickCount64 < Volatile.Read(ref horizonFailedUntil) ? HexHorizonState.Failed : HexHorizonState.Idle;

        // As far as whatever knows how far it is has got, a task in a terminal never does. With none that knows the line
        // is full and fainter, something is going and that's all that can be said
        float? progress = running ? work.NewestActive(measuredWork)?.Progress : null;
        frame.UpdateHorizon(chrome.Root, skin, top.Bounds.Height, chrome.Root.Bounds.Width, state, progress);
    }
}
