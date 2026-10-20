using Horizon.Core.Threading;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Work done off the editor's thread, every piece of it through one scheduler (the engine's WorkScheduler): reading the
// whole workspace, checking every file (F8), finding in files, looking for language servers, installing and updating
// them, asking GitHub. Checking every file and finding in files read and parsed every .hor on the editor's thread, a big
// workspace was Hex frozen for as long as that took. A piece of work known by a key runs once at a time (an update
// pressed twice is one update), says how far it got, and can be stopped. One that's asked to be seen has a card in the
// corner while it goes, a bar of how far it got and what it's doing, Cancel, and how it ended (Output when it wrote any),
// and the status bar says how many are going, a click for the list of them and stopping one.
internal sealed partial class HexScene
{
    private readonly WorkScheduler work = new();

    // How many of InBackground's are going, until what they came to has been taken in
    private int workGoing;

    // The cards of the work that's seen and the status bar's count of it, the engine's
    private UIWorkCards workCards = null!;

    // Numbers the work of InBackground, which has no key of its own
    private int backgroundNumber;

    /// <summary>Whether something is being done in the background right now.</summary>
    private bool Working => Volatile.Read(ref workGoing) > 0;

    /// <summary>
    /// Does something off the editor's thread and hands what it came to over on it. What the work reads of the editor
    /// it is given before it goes (a list of folders, a query), never reaches for while it runs.
    /// </summary>
    /// <param name="quiet">Whether it goes without saying so in the status bar, for what nobody asked for by name.</param>
    private void InBackground<T>(string doing, Func<T> work, Action<T> done, bool quiet = false)
    {
        Interlocked.Increment(ref workGoing);
        if (!quiet)
            Say(doing);

        T result = default!;
        this.work.Start($"background {++backgroundNumber}", doing, _ => result = work(), item =>
        {
            Interlocked.Decrement(ref workGoing);
            if (item.State == WorkState.Succeeded)
                done(result);
            else if (item.State == WorkState.Failed)
                Notify($"{doing} went wrong, {item.Error}", error: true);
        }, cancellable: false);
    }

    /// <summary>
    /// Starts a piece of work known by a key, or hands back the one of that key already going. <paramref name="seen"/>
    /// puts a card up for it while it goes. <paramref name="done"/> on the editor's thread once it's over.
    /// </summary>
    private WorkItem StartWork(string key, string title, Func<WorkContext, Task> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true) =>
        workCards.Start(key, title, what, done, seen, cancellable);

    /// <summary>The work of a blocking call, on a thread of the pool from start to end.</summary>
    private WorkItem StartWork(string key, string title, Action<WorkContext> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true) =>
        workCards.Start(key, title, what, done, seen, cancellable);

    /// <summary>A piece of work that's going brought to notice again, its card back up (or put up, if it had none or was closed).</summary>
    private void RenewWork(WorkItem item) => workCards.Renew(item);

    /// <summary>The cards of the work and the status bar's count of it, built with the rest of the status bar.</summary>
    private void BuildWorkItem()
    {
        work.Woken = Engine.WindowManager.Wake;
        workCards = new UIWorkCards(work, layout.Get<Notifications>("toasts"))
        {
            ShowOutput = ShowWorkOutput,
            Failed = text => Note(text, error: true),

            // InBackground's are quick and have no key of their own, they're said in the status bar instead
            Listed = item => !item.Key.StartsWith("background ", StringComparison.Ordinal)
        };
        workCards.AddTo(statusBar, chrome, "What Hex is doing in the background, click to see it and stop any of it");
    }

    /// <summary>What a piece of work wrote, in the output under the editor.</summary>
    private void ShowWorkOutput(WorkItem item)
    {
        Note($"{item.Title}:");
        foreach (string line in item.Output)
            Note("  " + line);
        ShowPanel(OUTPUT_PAGE);
    }

    /// <summary>Every tick, takes in whatever finished.</summary>
    private void TakeFinishedWork() => workCards.Update();
}
