using Hex.Dawn;
using Hex.Core;
using Horizon.UI.Components;

namespace Hex.Tests;

public sealed class HexAgentTests
{
    [Theory]
    [InlineData("--mcp", true)]
    [InlineData("--mcp-connect", true)]
    [InlineData("--mcp-connect --window", true)]
    [InlineData("--mcp --window", false)]
    [InlineData("--mcp --selftest", false)]
    public void Bridge_mode_is_explicit_and_does_not_depend_on_stdin(string command, bool expected) =>
        Assert.Equal(expected, McpBridge.IsBridgeMode(command.Split(' ')));

    [Fact]
    public void Requests_are_not_consumed_and_ids_survive_clearing()
    {
        var conversation = new HexAgentConversation();
        var request = conversation.Add("explain", "Explain", "test.cs", 2, 3, 2, "return value;", 7);
        Assert.Same(request, conversation.Find(request.Id));
        Assert.Same(request, conversation.Find(request.Id));
        Assert.True(request.Pending);
        request.Reply = "Returns the value.";
        Assert.False(request.Pending);
        conversation.Clear();
        Assert.Null(conversation.Find(request.Id));
        Assert.True(conversation.Add("chat", "Hello", "test.cs", 1, 1, 1, "", 0).Id > request.Id);
    }

    [Fact]
    public void Cancelled_requests_are_not_pending()
    {
        var conversation = new HexAgentConversation();
        var request = conversation.Add("change", "Change", "test.cs", 1, 1, 1, "original", 2);
        request.Cancelled = true;
        Assert.False(request.Pending);
        Assert.Equal("original", request.Source);
    }

    [Fact]
    public void Agent_hints_are_independent_dismissible_and_invalidated_by_typing()
    {
        var editor = new CodeEditor { Text = "one\ntwo" };
        editor.InlayHints = [new(0, 0, "parameter:")];
        editor.InlineHints = [new(0, "value = 1")];
        editor.AgentHints = [new("a", 0, 1, "An explanation"), new("b", 1, 2, "Another")];
        string? dismissed = null;
        editor.OnAgentHintDismissed = id => dismissed = id;
        Assert.True(editor.DismissAgentHint("a"));
        Assert.Equal("a", dismissed);
        Assert.Equal("b", Assert.Single(editor.AgentHints).Id);
        Assert.Single(editor.InlayHints);
        Assert.Single(editor.InlineHints);
        editor.Document.Insert("changed");
        Assert.Empty(editor.AgentHints);
        Assert.False(editor.DismissAgentHint("b"));
    }

    [Fact]
    public void Workspace_selection_uses_nearest_match_and_only_unambiguous_implicit_fallback()
    {
        string root = Path.GetFullPath("agent-workspace"), other = Path.GetFullPath("other-workspace");
        var parent = new McpListing(1, "pipe", "token", [root]);
        var child = new McpListing(2, "pipe", "token", [Path.Combine(root, "child")]);
        Assert.Equal(child, McpBridge.Select(Path.Combine(root, "child", "nested"), [parent, child], true));
        Assert.Null(McpBridge.Select(other, [parent], true));
        Assert.Equal(parent, McpBridge.Select(other, [parent], false));
        Assert.Null(McpBridge.Select(other, [parent, child], false));
    }

    [Fact]
    public async Task Concurrent_bridges_start_only_one_editor_and_recheck_after_lock()
    {
        string scope = Guid.NewGuid().ToString();
        int starts = 0;
        McpListing? published = null;
        using var ready = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            ready.Wait();
            return McpBridge.UnderLaunchLock(scope, () => published, () =>
            {
                Interlocked.Increment(ref starts);
                Thread.Sleep(50);
                return published = new McpListing(42, "pipe", "token", []);
            });
        })).ToArray();
        ready.Set();
        var results = await Task.WhenAll(calls);
        Assert.Equal(1, starts);
        Assert.All(results, listing => Assert.Equal(42, listing?.Pid));
    }
}
