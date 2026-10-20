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

    // The cards of the work that's seen, and the version of it each last showed
    private readonly Dictionary<WorkItem, (UINotification Card, int Shown)> workCards = [];

    private StatusItem tasksItem = null!;

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
    private WorkItem StartWork(string key, string title, Func<WorkContext, Task> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true)
    {
        bool fresh = !work.IsActive(key);
        WorkItem item = work.Start(key, title, what, done, cancellable);
        if (seen && fresh)
            ShowWork(item);
        else if (seen)
            RenewWork(item);
        return item;
    }

    /// <summary>A piece of work that's going brought to notice again, its card back up (or put up, if it had none or was closed).</summary>
    private void RenewWork(WorkItem item)
    {
        if (!item.IsActive)
            return;

        if (workCards.TryGetValue(item, out var shown) && !shown.Card.Closed)
            shown.Card.Renew();
        else
            ShowWork(item);
    }

    /// <summary>The work of a blocking call, on a thread of the pool from start to end.</summary>
    private WorkItem StartWork(string key, string title, Action<WorkContext> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true) =>
        StartWork(key, title, context => Task.Run(() => what(context), context.Token), done, seen, cancellable);

    // A card for a piece of work, kept up to date by UpdateWork
    private void ShowWork(WorkItem item)
    {
        UINotification card = toasts.Show(item.Title, "sync", null, 0.0f);
        card.Progress = float.NaN;
        if (item.Cancellable)
            card.Actions.Add(("Cancel", item.Cancel));
        workCards[item] = (card, -1);
    }

    /// <summary>The status bar's count of the work going, built with the rest of it.</summary>
    private void BuildWorkItem()
    {
        work.Woken = Engine.WindowManager.Wake;
        work.Ended += WorkEnded;
        tasksItem = statusBar.Add(string.Empty, right: true, pressed: ShowWorkMenu, tooltip: "What Hex is doing in the background, click to see it and stop any of it");
        tasksItem.IsVisible = () => work.Active > 0;
    }

    /// <summary>Every tick, what the work that ended left for the editor, and the cards and the count brought up to date.</summary>
    private void UpdateWork()
    {
        work.Pump();

        int going = work.Active;
        if (going > 0)
        {
            WorkItem? first = work.Items.FirstOrDefault(item => item.IsActive && !item.Key.StartsWith("background ", StringComparison.Ordinal));
            string text = first is null ? $"$(sync) {going} going" : going == 1 ? $"$(sync) {first.Title}" : $"$(sync) {first.Title} and {going - 1} more";
            if (first?.Progress is { } progress and < 1.0f)
                text += $" {progress:P0}";
            tasksItem.Text = text;
        }

        // Every tick, so nothing is made when there's nothing to show
        if (workCards.Count == 0)
            return;

        foreach (var (item, (card, shown)) in workCards.ToArray())
        {
            if (item.Version == shown)
                continue;

            card.Detail = item.Message;
            card.Progress = item.State == WorkState.Waiting ? float.NaN : item.Progress ?? float.NaN;
            workCards[item] = (card, item.Version);
        }
    }

    // A piece of work that ended, its card saying how, on the editor's thread
    private void WorkEnded(WorkItem item)
    {
        if (!workCards.Remove(item, out var shown))
            return;

        UINotification card = shown.Card;
        bool closed = card.Closed;
        IReadOnlyList<string> output = item.Output;
        string took = item.Started is { } started && item.Ended is { } ended ? $", {(ended - started).TotalSeconds:0.#} s" : string.Empty;

        // Closed by its x while it went is out of sight, unless it failed, which wants saying
        if (closed && item.State != WorkState.Failed)
            return;
        if (closed)
            card = toasts.Show(string.Empty, "error", ErrorColor, 0.0f);

        card.Progress = null;
        card.Actions.Clear();
        switch (item.State)
        {
            case WorkState.Succeeded:
                card.Text = $"{item.Title}, done{took}";
                card.Icon = "check";
                card.IconColor = null;
                card.Seconds = 6.0f;
                break;
            case WorkState.Cancelled:
                card.Text = $"{item.Title}, stopped";
                card.Icon = "stop";
                card.Seconds = 4.0f;
                break;
            default:
                card.Text = $"{item.Title} didn't work, {item.Error}";
                card.Icon = "error";
                card.IconColor = ErrorColor;
                card.Seconds = 0.0f;
                Note(card.Text, error: true);
                break;
        }

        card.Detail = item.State == WorkState.Succeeded ? string.Empty : item.Message;
        if (output.Count > 0)
            card.Actions.Add(("Output", () => ShowWorkOutput(item)));
        card.Renew();
    }

    /// <summary>What a piece of work wrote, in the output under the editor.</summary>
    private void ShowWorkOutput(WorkItem item)
    {
        Note($"{item.Title}:");
        foreach (string line in item.Output)
            Note("  " + line);
        ShowPanel(OUTPUT_PAGE);
    }

    /// <summary>The work going and what ended lately, a click on what's going stopping it.</summary>
    private void ShowWorkMenu()
    {
        var shown = new Menu("work");
        foreach (WorkItem item in work.Items.Where(item => !item.Key.StartsWith("background ", StringComparison.Ordinal)).Take(12))
        {
            string state = item.State switch
            {
                WorkState.Waiting => "waiting",
                WorkState.Running => item.Progress is { } progress ? progress.ToString("P0") : "going",
                WorkState.Succeeded => "done",
                WorkState.Cancelled => "stopped",
                _ => "failed"
            };

            if (item.IsActive && item.Cancellable)
                shown.Add($"Stop {item.Title}", item.Cancel, state, "stop");
            else
                shown.Add(item.Title, () => ShowWorkOutput(item), state, item.State == WorkState.Failed ? "error" : item.IsActive ? "sync" : "check");
        }

        if (shown.Items.Count == 0)
            shown.Add("Nothing going", () => { }, string.Empty).IsEnabled = () => false;
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>Every tick, takes in whatever finished.</summary>
    private void TakeFinishedWork() => UpdateWork();
}
