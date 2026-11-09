namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private IReadOnlyList<DebugThread> threads = [];
    private long threadRequest;

    private void LoadThreads()
    {
        threads = [];
        if (debugging is not { } session) return;
        int stop = stops;
        _ = Load();
        async Task Load()
        {
            try
            {
                var found = await session.Threads().ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (!ReferenceEquals(debugging, session) || !DebugStopped || stops != stop) return;
                    threads = found;
                    context.Panels.RefreshSide(HexSide.Debug);
                });
            }
            catch (Exception) { } // Some adapters cannot enumerate threads during termination.
        }
    }

    private UITreeItem ThreadSection()
    {
        var section = new UITreeItem(context.Strings.Said("debugging.threads"), "list-tree") { Expandable = true, Expanded = true, Children = [] };
        var session = debugging;
        int stop = stops;
        foreach (var thread in threads)
            section.Children.Add(new UITreeItem(thread.Name, debugging?.SelectedThread == thread.Id ? "debug-stackframe-focused" : "debug-stackframe-dot")
                { Detail = thread.Id.ToString(), Tag = new SideTag(Pressed: () => { if (DebugStopped && ReferenceEquals(debugging, session) && stops == stop) SelectThread(thread.Id); }) });
        return section;
    }

    internal void SelectThread(int id)
    {
        if (!DebugStopped || debugging is not { } session) return;
        int stop = stops;
        long request = ++threadRequest;
        _ = Select();
        async Task Select()
        {
            try
            {
                var selection = await session.PrepareThreadSelection(id).ConfigureAwait(false);
                debugWork.Enqueue(() =>
                {
                    if (selection is null || !ReferenceEquals(debugging, session) || !DebugStopped || stops != stop || threadRequest != request
                        || !session.TrySelectThread(selection)) return;
                    var frames = selection.Frames;
                    stops++;
                    stoppedFrames = frames;
                    stoppedFrame = 0;
                    variables.Reset();
                    frameNames.Clear();
                    ClearExecutionLine();
                    showingDisassembly &= IsDisassemblyPath(context.Documents.Current.Path);
                    for (int i = 0; !showingDisassembly && i < frames.Count; i++) if (OwnCode(frames[i])) { stoppedFrame = i; break; }
                    ShowFrame(stoppedFrame);
                    context.Panels.RefreshSide(HexSide.Debug);
                });
            }
            catch (Exception error)
            {
                debugWork.Enqueue(() =>
                {
                    if (ReferenceEquals(debugging, session) && DebugStopped && stops == stop && threadRequest == request)
                        context.Notices.Say(context.Strings.Said("debugging.thread_failed", ("why", error.Message)), error: true);
                });
            }
        }
    }
}
