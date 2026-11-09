using System.Numerics;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private sealed record SourceBreakpointView(WeakReference<HexDocument> Document, DebugSession Session, DebugSourceBreakpointSet Set,
        int Version, Action<Vector2>? OriginalMenu);
    private readonly Dictionary<string, SourceBreakpointView> sourceBreakpointViews = new(PlatformPaths.Comparer);

    private bool CurrentSourceBreakpointView(SourceBreakpointView view, HexDocument document) => debugging == view.Session && DebugStopped
        && view.Session.OwnsSourceBreakpoints(view.Set) && view.Document.TryGetTarget(out var target) && target == document
        && document.Path is {} path && sourceBreakpointViews.GetValueOrDefault(path) == view && document.Editor.Document.Version == view.Version;

    private bool CanToggleSourceBreakpointHere() => context.Documents.Current is { Path: {} path } document
        && sourceBreakpointViews.TryGetValue(path, out var view) && CurrentSourceBreakpointView(view, document);

    private void BindSourceBreakpoints(HexDocument document, DebugSession session, DebugSource source, DebugSourceBreakpointSet? existing = null)
    {
        if (document.Path is not {} path || !DebugStopped || debugging != session || !session.OwnsSource(source)) return;
        sourceBreakpointViews.TryGetValue(path, out var previous);
        var set = existing ?? (previous?.Session == session ? previous.Set : session.CreateSourceBreakpoints(source));
        Action<Vector2>? original = previous?.OriginalMenu ?? document.Editor.OnContextMenu;
        var view = new SourceBreakpointView(new(document), session, set, document.Editor.Document.Version, original);
        sourceBreakpointViews[path] = view;
        document.Editor.OnGutterPressed = line =>
        {
            if (CurrentSourceBreakpointView(view, document)) ToggleSourceBreakpoint(document, line);
        };
        document.Editor.OnContextMenu = point =>
        {
            int line = document.Editor.BreakpointLineAt(point);
            if (line >= 0 && CurrentSourceBreakpointView(view, document)) SourceBreakpointMenu(view, line);
            else original?.Invoke(point);
        };
        if (set.Source.Reference != source.Reference)
            ChangeSourceBreakpoints(session, () => session.RebindSourceBreakpoints(set, source));
        RefreshSourceBreakpointMarks();
    }

    private void ToggleSourceBreakpoint(HexDocument document, int line, bool enableOnly = false)
    {
        if (document.Path is not {} path || !sourceBreakpointViews.TryGetValue(path, out var view)
            || !CurrentSourceBreakpointView(view, document) || line < 0 || line >= document.Editor.Document.LineCount) return;
        ChangeSourceBreakpoints(view.Session, () => view.Session.ToggleSourceBreakpoint(view.Set, line, enableOnly));
    }

    private void ChangeSourceBreakpoints(DebugSession session, Func<Task<bool>> change)
    {
        _ = Change();
        async Task Change()
        {
            Exception? failed = null;
            bool changed = false;
            try { changed = await change().ConfigureAwait(false); }
            catch (Exception error) { failed = error; }
            debugWork.Enqueue(() =>
            {
                if (debugging != session) return;
                var unverified = changed ? session.SourceBreakpointSets.SelectMany(set => set.Breakpoints).FirstOrDefault(point => point.Enabled && !point.Verified) : null;
                if (failed is not null || unverified is not null)
                    context.Notices.Say(context.Strings.Said("debugging.source_breakpoint_failed", ("why", failed?.Message ?? unverified?.Message
                        ?? context.Strings.Said("debugging.source_breakpoint_unverified"))), error: true);
                RefreshSourceBreakpointMarks();
                context.Panels.RefreshSide(HexSide.Debug);
            });
        }
    }

    private void RefreshSourceBreakpointMarks()
    {
        foreach (var view in sourceBreakpointViews.Values)
        {
            if (debugging != view.Session || !view.Document.TryGetTarget(out var document) || document.Editor.Document.Version != view.Version) continue;
            var points = view.Set.Breakpoints;
            document.Editor.Breakpoints = new SortedSet<int>(points.Select(point => point.Line));
            document.Editor.BreakpointMarks = points.GroupBy(point => point.Line).ToDictionary(group => group.Key,
                group => group.Any(point => point.Enabled && point.Verified) ? CodeBreakpointMark.Plain : CodeBreakpointMark.Disabled);
        }
    }

    private void SourceBreakpointMenu(SourceBreakpointView view, int line)
    {
        if (!view.Document.TryGetTarget(out var document) || !CurrentSourceBreakpointView(view, document)) return;
        var point = view.Set.Breakpoints.FirstOrDefault(point => point.Line == line || point.RequestedLine == line);
        var menu = new Menu("source-reference-breakpoint");
        bool Current() => CurrentSourceBreakpointView(view, document);
        if (point is null)
            menu.Add(context.Strings.Said("debugging.insert_breakpoint"), () => { if (Current()) ToggleSourceBreakpoint(document, line); }, "F9", "debug-breakpoint");
        else
        {
            menu.Add(context.Strings.Said(point.Enabled ? "debugging.disable_breakpoint" : "debugging.enable_breakpoint"), () =>
            { if (Current()) ChangeSourceBreakpoints(view.Session, () => view.Session.EnableSourceBreakpoint(view.Set, point.RequestedLine, !point.Enabled)); }, icon: "circle");
            menu.Add(context.Strings.Said("debugging.delete_breakpoint"), () =>
            { if (Current()) ChangeSourceBreakpoints(view.Session, () => view.Session.RemoveSourceBreakpoint(view.Set, point.RequestedLine)); }, icon: "trash");
        }
        context.Windows.Menu(menu);
    }

    private void AddSourceReferenceBreakpointRows(UITreeItem section)
    {
        if (debugging is not {} session) return;
        foreach (var set in session.SourceBreakpointSets)
            foreach (var point in set.Breakpoints)
            {
                string detail = !point.Enabled ? context.Strings.Said("debugging.off") : point.Verified
                    ? context.Strings.Said("debugging.source_breakpoint_verified") : point.Message ?? context.Strings.Said("debugging.source_breakpoint_unverified");
                section.Children!.Add(new UITreeItem($"{DebuggerSourceName(set.Source)}:{point.Line + 1}", point.Enabled && point.Verified ? "debug-breakpoint" : "circle")
                {
                    Detail = context.Strings.Said("debugging.source_breakpoint_session") + " · " + detail,
                    Tooltip = (set.Source.Path ?? set.Source.Name) + "\n" + detail, IconColor = context.Panels.Error,
                    Color = point.Enabled ? null : context.Panels.Dim,
                    Tag = new SideTag(Pressed: () =>
                    {
                        if (debugging != session || !DebugStopped || !session.OwnsSourceBreakpoints(set)) return;
                        var entry = sourceBreakpointViews.FirstOrDefault(pair => pair.Value.Set == set);
                        if (entry.Key is null) return;
                        context.Documents.OpenAt(entry.Key, point.Line, 0);
                        if (context.Documents.Find(entry.Key) is {} document) BindSourceBreakpoints(document, session, set.Source, set);
                    }, Menu: _ =>
                    {
                        var view = sourceBreakpointViews.Values.FirstOrDefault(view => view.Set == set);
                        if (view is not null) SourceBreakpointMenu(view, point.Line);
                    })
                });
            }
    }

    private void ClearSourceBreakpointViews()
    {
        foreach (var view in sourceBreakpointViews.Values)
            if (view.Document.TryGetTarget(out var document))
            {
                document.Editor.OnGutterPressed = null;
                document.Editor.OnContextMenu = view.OriginalMenu;
                document.Editor.Breakpoints = null;
                document.Editor.BreakpointMarks = null;
            }
        sourceBreakpointViews.Clear();
    }
}
