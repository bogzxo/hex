using System.Numerics;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The variables of the frame looked at, asked for once and kept until the frame or stop changes. The adapter can
// answer an old question after a step, a restart, or a return to the same frame. Those answers belong to the old view,
// including the children of rows opened while it was on screen.
internal sealed class HexDebugVariables(Func<string, string> text, Func<Exception, string> failure, Func<Vector4> dim,
    Action<Action> post, Action refresh, Action refreshRows)
{
    private object? owner;
    private int frame;
    private long generation;
    private List<UITreeItem>? shown;
    private bool asking;

    private string[] watchExpressions = [];
    private List<UITreeItem>? watchesShown;
    private bool watchesAsking;
    private long watchRequest;

    internal void ForgetWatches()
    {
        watchRequest++;
        watchExpressions = [];
        watchesShown = null;
        watchesAsking = false;
    }

    internal void Reset()
    {
        generation++;
        ForgetWatches();
        owner = null;
        shown = null;
        asking = false;
    }

    private void Select(object session, int frameId)
    {
        if (ReferenceEquals(owner, session) && frame == frameId)
            return;
        Reset();
        owner = session;
        frame = frameId;
    }

    private bool Current(object session, long asked) => ReferenceEquals(owner, session) && generation == asked;

    // The first scope's variables as they are (the locals), the rest folded under their names. lldb-dap says
    // every global of the program and the registers too, and flat they buried the locals under __imp_ rows.
    internal List<UITreeItem> Rows(object session, int frameId, Func<int, Task<IReadOnlyList<DebugScope>>> scopes,
        Func<int, Task<IReadOnlyList<DebugVariable>>> variables, Action<int, DebugVariable>? edit = null, bool hexadecimal = false)
    {
        Select(session, frameId);
        if (shown is not null)
            return shown;
        if (!asking)
        {
            asking = true;
            long asked = generation;
            _ = Task.Run(async () =>
            {
                var rows = new List<UITreeItem>();
                try
                {
                    IReadOnlyList<DebugScope> found = await scopes(frameId);
                    for (int i = 0; i < found.Count; i++)
                    {
                        if (i > 0)
                        {
                            rows.Add(Expanding(new UITreeItem(found[i].Name, "symbol-namespace") { Expandable = true }, session, asked, found[i].Reference, variables, edit, hexadecimal));
                            continue;
                        }
                        // Not the compiler's own (dmd's __r103 and __key104 for a foreach), nobody wrote those.
                        foreach (DebugVariable variable in await variables(found[i].Reference))
                            if (!variable.Name.StartsWith("__", StringComparison.Ordinal))
                                rows.Add(Item(session, asked, variable, variables, found[i].Reference, edit, hexadecimal));
                    }
                }
                catch (Exception e)
                {
                    rows.Add(Note(failure(e)));
                }
                post(() =>
                {
                    if (!Current(session, asked))
                        return;
                    shown = rows.Count > 0 ? rows : [Note(text("debugging.nothing_in_scope"))];
                    refresh();
                });
            });
        }
        return [Note(text("debugging.asking"))];
    }

    // Watches share the frame's lifetime, but changing the expression list retires only their own request.
    // Null means a request is already on its way; a redraw must not ask it again.
    internal List<UITreeItem>? Watches(object session, int frameId, IReadOnlyList<string> expressions,
        Func<string, int, Task<DebugVariable?>> evaluate, Func<int, Task<IReadOnlyList<DebugVariable>>> variables) =>
        Watches(session, frameId, expressions, async (expression, frame) => new DebugEvaluation(await evaluate(expression, frame), null), variables);

    // The same with what the debugger said when it couldn't evaluate one, on the row in place of its value
    internal List<UITreeItem>? Watches(object session, int frameId, IReadOnlyList<string> expressions,
        Func<string, int, Task<DebugEvaluation>> evaluate, Func<int, Task<IReadOnlyList<DebugVariable>>> variables)
    {
        Select(session, frameId);
        if (!watchExpressions.SequenceEqual(expressions))
        {
            ForgetWatches();
            watchExpressions = [.. expressions];
        }
        if (watchesShown is not null || watchesAsking)
            return watchesShown;

        watchesAsking = true;
        string[] asked = watchExpressions;
        long request = watchRequest, view = generation;
        var item = Items(session, variables);
        _ = Task.Run(async () =>
        {
            var rows = new List<UITreeItem>();
            try
            {
                foreach (string expression in asked)
                {
                    DebugEvaluation evaluated = await evaluate(expression, frameId);
                    string why = evaluated.Error ?? text("debugging.not_here");
                    rows.Add(evaluated.Value is { } said ? item(said)
                        : new UITreeItem($"{expression} = ?", "symbol-variable") { Color = dim(), Detail = why, Tooltip = why });
                }
            }
            catch (Exception e)
            {
                rows.Add(Note(failure(e)));
            }
            post(() =>
            {
                if (!Current(session, view) || watchRequest != request)
                    return;
                watchesShown = rows;
                watchesAsking = false;
                refresh();
            });
        });
        return null;
    }

    private UITreeItem Note(string said) => new(said) { Color = dim() };

    // Watch rows share the same lazy children and lifetime as the variables of this frame.
    internal Func<DebugVariable, UITreeItem> Items(object session, Func<int, Task<IReadOnlyList<DebugVariable>>> variables)
    {
        long asked = generation;
        return variable =>
        {
            UITreeItem item = Item(session, asked, variable, variables);
            item.Tag = (item.Tag as SideTag ?? new SideTag()) with { DebugValue = variable, DebugCurrent = () => Current(session, asked) };
            return item;
        };
    }

    private UITreeItem Item(object session, long asked, DebugVariable variable, Func<int, Task<IReadOnlyList<DebugVariable>>> variables, int parent = 0, Action<int, DebugVariable>? edit = null, bool hexadecimal = false)
    {
        var item = new UITreeItem($"{variable.Name} = {variable.Value}", "symbol-variable")
        {
            Detail = variable.ReadOnly ? string.Join(" ? ", new[] { variable.Type, text("debugging.read_only") }.Where(value => !string.IsNullOrEmpty(value))) : variable.Type ?? string.Empty,
            Tooltip = $"{variable.Name} = {variable.Value}",
            Expandable = variable.Reference > 0
        };
        if (variable.Reference > 0)
        {
            if (variable.IndexedVariables is > 100 && session is DebugSession adapter)
                Paging(item, adapter, asked, variable, variables, edit, hexadecimal);
            else Expanding(item, session, asked, variable.Reference, variables, edit, hexadecimal);
        }
        if (edit is not null && parent > 0 && !variable.ReadOnly)
        {
            Action<System.Numerics.Vector2> menu = _ => { if (Current(session, asked)) edit(parent, variable); };
            item.Tag = item.Tag is SideTag tag ? tag with { Menu = menu } : new SideTag(Menu: menu);
        }
        if (!string.IsNullOrWhiteSpace(variable.EvaluateName))
            item.Tag = (item.Tag as SideTag ?? new SideTag()) with { DebugExpression = variable.EvaluateName, DebugCurrent = () => Current(session, asked) };
        item.Tag = (item.Tag as SideTag ?? new SideTag()) with { DebugValue = variable, DebugContainer = parent, DebugCurrent = () => Current(session, asked) };
        return item;
    }

    private void Paging(UITreeItem item, DebugSession session, long asked, DebugVariable variable,
        Func<int, Task<IReadOnlyList<DebugVariable>>> variables, Action<int, DebugVariable>? edit, bool hexadecimal)
    {
        const int size = 100;
        bool loading = false;
        int loaded = 0;
        UITreeItem? more = null, errorRow = null;
        void Load(int start)
        {
            if (!Current(session, asked) || loading || start != loaded) return;
            loading = true;
            _ = Task.Run(async () =>
            {
                List<DebugVariable> named = [];
                IReadOnlyList<DebugVariable> page = [];
                Exception? error = null;
                try
                {
                    if (start == 0 && variable.NamedVariables != 0)
                        named.AddRange(await session.Variables(variable.Reference, hexadecimal, filter: "named"));
                    page = await session.Variables(variable.Reference, hexadecimal, start, size, "indexed");
                }
                catch (Exception e) { error = e; }
                post(() =>
                {
                    if (!Current(session, asked)) return;
                    loading = false;
                    if (start == 0) item.Children = [];
                    if (more is not null) item.Children!.Remove(more);
                    if (errorRow is not null) { item.Children!.Remove(errorRow); errorRow = null; }
                    foreach (var child in named.Concat(page))
                        item.Children!.Add(Item(session, asked, child, variables, variable.Reference, edit, hexadecimal));
                    if (error is not null)
                    {
                        errorRow = Note(failure(error));
                        item.Children!.Add(errorRow);
                        more = new UITreeItem(text("debugging.retry_variables"), "refresh") { Tag = new SideTag(Pressed: () => Load(start)) };
                        item.Children.Add(more);
                    }
                    int next = start + page.Count;
                    if (error is null) loaded = next;
                    if (error is null && page.Count == size && next < variable.IndexedVariables)
                    {
                        more = new UITreeItem(text("debugging.load_more_variables"), "ellipsis") { Tag = new SideTag(Pressed: () => Load(next)) };
                        item.Children!.Add(more);
                    }
                    refreshRows();
                });
            });
        }
        item.Tag = new SideTag(Expanding: opened =>
        {
            if (!Current(session, asked) || opened.Children is not null) return;
            opened.Children = [Note(text("debugging.asking"))];
            Load(0);
        });
    }

    // A row whose children are the variables of a reference (a variable's, a scope's), asked for as it's opened.
    private UITreeItem Expanding(UITreeItem item, object session, long asked, int reference,
        Func<int, Task<IReadOnlyList<DebugVariable>>> variables, Action<int, DebugVariable>? edit = null, bool hexadecimal = false)
    {
        bool loading = false;
        void Load()
        {
            if (!Current(session, asked) || loading) return;
            loading = true;
            item.Children = [Note(text("debugging.asking"))];
            _ = Task.Run(async () =>
            {
                var inside = new List<UITreeItem>();
                try
                {
                    foreach (DebugVariable child in await variables(reference))
                        inside.Add(Item(session, asked, child, variables, reference, edit, hexadecimal));
                }
                catch (Exception error)
                {
                    inside.Add(Note(failure(error)));
                    inside.Add(new UITreeItem(text("debugging.retry_variables"), "refresh") { Tag = new SideTag(Pressed: Load) });
                }
                post(() =>
                {
                    if (!Current(session, asked)) return;
                    loading = false;
                    item.Children = inside;
                    refreshRows();
                });
            });
        }
        item.Tag = new SideTag(Expanding: opened => { if (opened.Children is null) Load(); });
        return item;
    }
}
