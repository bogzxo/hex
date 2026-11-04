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
        Func<int, Task<IReadOnlyList<DebugVariable>>> variables)
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
                            rows.Add(Expanding(new UITreeItem(found[i].Name, "symbol-namespace") { Expandable = true }, session, asked, found[i].Reference, variables));
                            continue;
                        }
                        // Not the compiler's own (dmd's __r103 and __key104 for a foreach), nobody wrote those.
                        foreach (DebugVariable variable in await variables(found[i].Reference))
                            if (!variable.Name.StartsWith("__", StringComparison.Ordinal))
                                rows.Add(Item(session, asked, variable, variables));
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
        Func<string, int, Task<DebugVariable?>> evaluate, Func<int, Task<IReadOnlyList<DebugVariable>>> variables)
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
                    DebugVariable? value = await evaluate(expression, frameId);
                    rows.Add(value is { } said ? item(said)
                        : new UITreeItem($"{expression} = ?", "symbol-variable") { Color = dim(), Detail = text("debugging.not_here") });
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
        return variable => Item(session, asked, variable, variables);
    }

    private UITreeItem Item(object session, long asked, DebugVariable variable, Func<int, Task<IReadOnlyList<DebugVariable>>> variables)
    {
        var item = new UITreeItem($"{variable.Name} = {variable.Value}", "symbol-variable")
        {
            Detail = variable.Type ?? string.Empty,
            Tooltip = $"{variable.Name} = {variable.Value}",
            Expandable = variable.Reference > 0
        };
        return variable.Reference > 0 ? Expanding(item, session, asked, variable.Reference, variables) : item;
    }

    // A row whose children are the variables of a reference (a variable's, a scope's), asked for as it's opened.
    private UITreeItem Expanding(UITreeItem item, object session, long asked, int reference,
        Func<int, Task<IReadOnlyList<DebugVariable>>> variables)
    {
        item.Tag = new SideTag(Expanding: opened =>
        {
            if (!Current(session, asked) || opened.Children is not null)
                return;
            opened.Children = [Note(text("debugging.asking"))];
            _ = Task.Run(async () =>
            {
                var inside = new List<UITreeItem>();
                try
                {
                    foreach (DebugVariable child in await variables(reference))
                        inside.Add(Item(session, asked, child, variables));
                }
                catch (Exception)
                {
                    // gone on since, nothing to show
                }
                post(() =>
                {
                    if (!Current(session, asked))
                        return;
                    opened.Children = inside;
                    refreshRows();
                });
            });
        });
        return item;
    }
}
