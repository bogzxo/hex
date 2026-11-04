using System.Numerics;
using System.Threading.Channels;

using Hex.Dawn;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public sealed class HexDebugVariablesTests
{
    private sealed class View
    {
        private readonly Channel<Action> work = Channel.CreateUnbounded<Action>();
        internal int Refreshes, RowRefreshes;
        internal readonly HexDebugVariables Variables;

        internal View() => Variables = new(key => key, e => e.Message, () => Vector4.One,
            action => work.Writer.TryWrite(action), () => Refreshes++, () => RowRefreshes++);

        internal async Task Apply() => (await work.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))();
    }

    private static Task<IReadOnlyList<DebugScope>> Locals(int frame) => Task.FromResult<IReadOnlyList<DebugScope>>([new("Locals", frame)]);
    private static Task<IReadOnlyList<DebugVariable>> Value(string value) => Task.FromResult<IReadOnlyList<DebugVariable>>([new("answer", value, "int", 0)]);
    private static TaskCompletionSource<IReadOnlyList<DebugScope>> PendingScopes() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_old_stop_or_session_cannot_replace_the_same_frame_after_it(bool restart)
    {
        var view = new View();
        object oldSession = new(), currentSession = restart ? new() : oldSession;
        var oldScopes = PendingScopes();
        view.Variables.Rows(oldSession, 10, _ => oldScopes.Task, _ => Value("old"));
        view.Variables.Reset(); // continued, then stopped again, or the session ended and another started
        view.Variables.Rows(currentSession, 10, Locals, _ => Value("current"));
        await view.Apply();
        oldScopes.SetResult([new("Locals", 10)]);
        await view.Apply();
        Assert.Equal("answer = current", Assert.Single(view.Variables.Rows(currentSession, 10, Locals, _ => Value("unused"))).Text);
        Assert.Equal(1, view.Refreshes);
    }

    [Fact]
    public async Task A_new_session_alone_retires_the_old_sessions_pending_answer()
    {
        var view = new View();
        object oldSession = new(), currentSession = new();
        var oldScopes = PendingScopes();
        view.Variables.Rows(oldSession, 10, _ => oldScopes.Task, _ => Value("old"));
        view.Variables.Rows(currentSession, 10, Locals, _ => Value("current"));
        await view.Apply();
        oldScopes.SetResult([new("Locals", 10)]);
        await view.Apply();
        Assert.Equal("answer = current", Assert.Single(view.Variables.Rows(currentSession, 10, Locals, _ => Value("unused"))).Text);
        Assert.Equal(1, view.Refreshes);
    }

    [Fact]
    public async Task A_watch_answer_built_after_its_frame_was_left_keeps_its_original_child_lifetime()
    {
        var view = new View();
        object session = new();
        view.Variables.Rows(session, 10, Locals, _ => Value("current"));
        await view.Apply();
        int requests = 0;
        Task<IReadOnlyList<DebugVariable>> Children(int _) { requests++; return Value("old child"); }
        var item = view.Variables.Items(session, Children);
        view.Variables.Reset();
        view.Variables.Rows(session, 10, Locals, _ => Value("new stop"));
        await view.Apply();
        UITreeItem oldWatch = item(new("box", "{}", "Box", 2));
        ((SideTag)oldWatch.Tag!).Expanding!(oldWatch);
        Assert.Null(oldWatch.Children);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Leaving_a_frame_and_returning_rejects_the_first_visit_even_with_the_same_session_and_stop()
    {
        var view = new View();
        object session = new();
        var oldScopes = PendingScopes();
        view.Variables.Rows(session, 10, _ => oldScopes.Task, _ => Value("first visit"));
        view.Variables.Rows(session, 20, Locals, _ => Value("other frame"));
        await view.Apply();
        view.Variables.Rows(session, 10, Locals, _ => Value("return visit"));
        await view.Apply();
        oldScopes.SetResult([new("Locals", 10)]);
        await view.Apply();
        Assert.Equal("answer = return visit", Assert.Single(view.Variables.Rows(session, 10, Locals, _ => Value("unused"))).Text);
        Assert.Equal(2, view.Refreshes);
    }

    [Fact]
    public async Task Rebuilding_the_side_while_waiting_asks_once_and_keeps_locals_first_and_other_scopes_lazy()
    {
        var view = new View();
        object session = new();
        int requests = 0;
        var pending = PendingScopes();
        Task<IReadOnlyList<DebugScope>> Scopes(int _) { Interlocked.Increment(ref requests); return pending.Task; }
        Task<IReadOnlyList<DebugVariable>> Variables(int reference) => Task.FromResult<IReadOnlyList<DebugVariable>>(
            reference == 1 ? [new("__compiler", "hidden", null, 0), new("box", "{}", "Box", 3)] : [new("width", "42", "int", 0)]);
        view.Variables.Rows(session, 10, Scopes, Variables);
        view.Variables.Rows(session, 10, Scopes, Variables);
        pending.SetResult([new("Locals", 1), new("Globals", 2)]);
        await view.Apply();
        var rows = view.Variables.Rows(session, 10, Scopes, Variables);
        Assert.Equal(1, requests);
        Assert.Equal(["box = {}", "Globals"], rows.Select(row => row.Text));
        Assert.Equal("Box", rows[0].Detail);
        Assert.Equal("box = {}", rows[0].Tooltip);
        Assert.Null(rows[1].Children);
        ((SideTag)rows[1].Tag!).Expanding!(rows[1]);
        await view.Apply();
        Assert.Equal("width = 42", Assert.Single(rows[1].Children!).Text);
        Assert.Equal(1, view.RowRefreshes);
    }

    [Fact]
    public async Task Ending_a_view_rejects_pending_children_and_does_not_expand_retired_rows_again()
    {
        var view = new View();
        object session = new();
        var children = new TaskCompletionSource<IReadOnlyList<DebugVariable>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        Task<IReadOnlyList<DebugVariable>> Variables(int reference)
        {
            if (reference == 1) return Task.FromResult<IReadOnlyList<DebugVariable>>([new("box", "{}", "Box", 2)]);
            Interlocked.Increment(ref requests);
            expanded.TrySetResult();
            return children.Task;
        }
        view.Variables.Rows(session, 1, Locals, Variables);
        await view.Apply();
        UITreeItem box = Assert.Single(view.Variables.Rows(session, 1, Locals, Variables));
        ((SideTag)box.Tag!).Expanding!(box);
        await expanded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Variables.Reset();
        children.SetResult([new("width", "old", "int", 0)]);
        await view.Apply();
        Assert.Equal("debugging.asking", Assert.Single(box.Children!).Text);
        Assert.Equal(0, view.RowRefreshes);
        box.Children = null;
        ((SideTag)box.Tag!).Expanding!(box);
        Assert.Null(box.Children);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_watch_expressions_retires_the_old_list_even_in_the_same_frame(bool explicitlyInvalidated)
    {
        var view = new View();
        object session = new();
        var oldValue = new TaskCompletionSource<DebugVariable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        Task<DebugVariable?> Evaluate(string expression, int _)
        {
            Interlocked.Increment(ref requests);
            if (expression == "old") { started.TrySetResult(); return oldValue.Task; }
            return Task.FromResult<DebugVariable?>(new(expression, "current", "int", 0));
        }
        Assert.Null(view.Variables.Watches(session, 10, ["old"], Evaluate, _ => Value("unused")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(view.Variables.Watches(session, 10, ["old"], Evaluate, _ => Value("unused")));
        Assert.Equal(1, requests);
        if (explicitlyInvalidated) view.Variables.ForgetWatches();
        Assert.Null(view.Variables.Watches(session, 10, ["new"], Evaluate, _ => Value("unused")));
        await view.Apply();
        oldValue.SetResult(new("old", "retired", "int", 0));
        await view.Apply();
        Assert.Equal("new = current", Assert.Single(view.Variables.Watches(session, 10, ["new"], Evaluate, _ => Value("unused"))!).Text);
        Assert.Equal(2, requests);
        Assert.Equal(1, view.Refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watches_and_locals_establish_the_same_frame_before_capturing_their_generation(bool watchesFirst)
    {
        var view = new View();
        object session = new();
        var answer = new TaskCompletionSource<DebugVariable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (watchesFirst) view.Variables.Watches(session, 10, ["box"], (_, _) => answer.Task, _ => Value("unused"));
        view.Variables.Rows(session, 10, Locals, _ => Value("local"));
        await view.Apply();
        if (!watchesFirst) view.Variables.Watches(session, 10, ["box"], (_, _) => answer.Task, _ => Value("unused"));
        answer.SetResult(new("box", "42", "int", 0));
        await view.Apply();
        Assert.Equal("box = 42", Assert.Single(view.Variables.Watches(session, 10, ["box"], (_, _) => answer.Task, _ => Value("unused"))!).Text);
        Assert.Equal(2, view.Refreshes);
    }

    [Theory]
    [InlineData(false, "debugging.nothing_in_scope")]
    [InlineData(true, "adapter failed")]
    public async Task Empty_scopes_and_adapter_errors_still_say_what_happened(bool fail, string expected)
    {
        var view = new View();
        object session = new();
        Task<IReadOnlyList<DebugScope>> Scopes(int _) => fail
            ? Task.FromException<IReadOnlyList<DebugScope>>(new IOException("adapter failed"))
            : Task.FromResult<IReadOnlyList<DebugScope>>([]);
        view.Variables.Rows(session, 10, Scopes, _ => Value("unused"));
        await view.Apply();
        Assert.Equal(expected, Assert.Single(view.Variables.Rows(session, 10, Scopes, _ => Value("unused"))).Text);
    }
}
