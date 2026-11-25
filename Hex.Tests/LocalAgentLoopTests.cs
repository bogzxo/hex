using System.Runtime.CompilerServices;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>The local agent's loop, its protocol lines and its session store, against a scripted model and a recording tool host.</summary>
public sealed class LocalAgentLoopTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-la3-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    /// <summary>A model whose every call is answered by the script from the number of the call and the conversation it was shown.</summary>
    private sealed class ScriptedChat(Func<int, IReadOnlyList<AgentMessage>, AgentDelta[]> script) : IAgentChat
    {
        public int Calls;
        public List<List<AgentMessage>> Seen = [];
        public List<string[]> ToolsSeen = [];

        public async IAsyncEnumerable<AgentDelta> Chat(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, [EnumeratorCancellation] CancellationToken cancel)
        {
            Seen.Add([.. messages]);
            ToolsSeen.Add([.. tools.Select(t => t.Name)]);
            foreach (var delta in script(Calls++, messages))
            {
                cancel.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private sealed class FakeTools(Func<string, string, AgentToolResult>? reply = null) : IToolHost
    {
        public List<(string Name, string Args)> Called = [];
        public IReadOnlyList<ToolSpec> Tools { get; } =
        [
            new("hex_read", "Reads.", "{}"),
            new("hex_edit", "Edits.", "{}"),
            new("hex_terminal_run", "Runs.", "{}"),
        ];

        public Task<AgentToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancel)
        {
            Called.Add((name, argumentsJson));
            return Task.FromResult(reply?.Invoke(name, argumentsJson) ?? new AgentToolResult("ok " + name, false));
        }
    }

    private sealed class Rig
    {
        public required LocalAgentLoop Loop;
        public List<string> Lines = [];
        public List<(string Id, string Tool, string Input)> Asked = [];
    }

    private Rig Make(ScriptedChat chat, FakeTools tools, string approvals = "all", bool allow = true, int context = 0, LocalAgentStore? store = null, string? session = null, string profile = "write")
    {
        var lines = new List<string>();
        var asked = new List<(string, string, string)>();
        var options = new LocalAgentOptions("ollama", "http://localhost", "m", null, PredictionTuning.Usual, context, 1024, approvals, folder, 1, session, profile);
        var loop = new LocalAgentLoop(chat, tools, options, lines.Add, (id, tool, input, _) =>
        {
            asked.Add((id, tool, input));
            return Task.FromResult(allow);
        }, store);
        return new Rig { Loop = loop, Lines = lines, Asked = asked };
    }

    private static AgentDelta Call(string id, string name, string args = "{}") => new AgentDelta.Call(new ToolCall(id, name, args));

    private static AgentDelta[] Answer(string text) => [new AgentDelta.Text(text), new AgentDelta.Done("stop")];

    private static string Type(string line) => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()!;

    private static string[] Types(Rig rig) => rig.Lines.Select(Type).ToArray();

    [Fact]
    public async Task A_turn_without_calls_answers_and_stops()
    {
        var rig = Make(new ScriptedChat((_, _) => [new AgentDelta.Thinking("hm"), new AgentDelta.Text("Hel"), new AgentDelta.Text("lo"), new AgentDelta.Usage(10, 2, 0.5), new AgentDelta.Done("stop")]), new FakeTools());
        await rig.Loop.RunTurnAsync("hi", default);

        Assert.Equal(["thinking", "text", "text", "usage", "done"], Types(rig));
        Assert.Equal("stop", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString());
        Assert.Equal(["system", "user", "assistant"], rig.Loop.Conversation.Select(m => m.Role));
        Assert.Equal("Hello", rig.Loop.Conversation[^1].Content);
    }

    [Fact]
    public async Task A_tool_call_runs_then_the_answer_comes()
    {
        var chat = new ScriptedChat((n, _) => n == 0 ? [Call("c1", "hex_read", "{\"path\":\"a.cs\"}"), new AgentDelta.Done("tool_calls")] : Answer("done"));
        var tools = new FakeTools();
        var rig = Make(chat, tools);
        await rig.Loop.RunTurnAsync("look", default);

        Assert.Equal([("hex_read", "{\"path\":\"a.cs\"}")], tools.Called);
        Assert.Equal(["tool", "tool_done", "text", "done"], Types(rig));
        var tool = JsonDocument.Parse(rig.Lines[0]).RootElement;
        Assert.Equal("a.cs", tool.GetProperty("input").GetProperty("path").GetString());
        var message = rig.Loop.Conversation.Single(m => m.Role == "tool");
        Assert.Equal("c1", message.CallId);
        Assert.Equal("hex_read", message.Name);
        Assert.Equal("ok hex_read", message.Content);
        Assert.Single(rig.Loop.Conversation.Single(m => m.Calls != null).Calls!);
    }

    [Fact]
    public async Task An_unknown_tool_and_bad_arguments_go_back_as_errors()
    {
        var chat = new ScriptedChat((n, _) => n switch
        {
            0 => [Call("a", "nope"), Call("b", "hex_read", "{not json"), Call("c", "hex_read", "[1]")],
            _ => Answer("ok"),
        });
        var tools = new FakeTools();
        var rig = Make(chat, tools);
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Empty(tools.Called);
        var results = rig.Loop.Conversation.Where(m => m.Role == "tool").ToList();
        Assert.Equal(3, results.Count);
        Assert.Contains("no tool called nope", results[0].Content);
        Assert.Contains("not valid JSON", results[1].Content);
        Assert.Contains("JSON object", results[2].Content);
        Assert.Equal(3, rig.Lines.Count(l => Type(l) == "tool_done" && JsonDocument.Parse(l).RootElement.GetProperty("error").GetBoolean()));
    }

    [Theory]
    [InlineData("edits", "hex_terminal_run", true)]
    [InlineData("edits", "hex_edit", false)]
    [InlineData("edits", "hex_read", false)]
    [InlineData("ask", "hex_edit", true)]
    [InlineData("ask", "hex_read", false)]
    [InlineData("all", "hex_terminal_run", false)]
    public async Task Approvals_follow_the_mode(string mode, string tool, bool asks)
    {
        var chat = new ScriptedChat((n, _) => n == 0 ? [Call("c1", tool)] : Answer("ok"));
        var tools = new FakeTools();
        var rig = Make(chat, tools, mode);
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(asks, rig.Asked.Count == 1);
        Assert.Equal(asks, rig.Lines.Any(l => Type(l) == "approval"));
        Assert.Single(tools.Called);
    }

    [Fact]
    public async Task A_denied_call_is_not_run_and_the_model_is_told()
    {
        var chat = new ScriptedChat((n, _) => n == 0 ? [Call("c1", "hex_edit", "{}")] : Answer("ok"));
        var tools = new FakeTools();
        var rig = Make(chat, tools, "ask", allow: false);
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Empty(tools.Called);
        Assert.Equal("The person didn't allow this.", rig.Loop.Conversation.Single(m => m.Role == "tool").Content);
        Assert.Equal("stop", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_same_call_three_times_stops_the_turn()
    {
        var chat = new ScriptedChat((_, _) => [Call("c" + Guid.NewGuid(), "hex_read", "{\"p\":1}")]);
        var tools = new FakeTools();
        var rig = Make(chat, tools);
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(2, tools.Called.Count);
        Assert.Contains(rig.Lines, l => l.Contains("stopped a repeated call"));
        Assert.Equal("repeat", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Forty_model_calls_end_the_turn()
    {
        int n = 0;
        var chat = new ScriptedChat((_, _) => [Call("c" + n, "hex_read", "{\"n\":" + n++ + "}")]);
        var rig = Make(chat, new FakeTools());
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(40, chat.Calls);
        Assert.Equal("limit", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString());
        Assert.Contains(rig.Lines, l => Type(l) == "note");
    }

    [Fact]
    public async Task A_conversation_over_three_quarters_of_the_window_is_trimmed()
    {
        string big = new('x', 3000);
        int n = 0;
        var chat = new ScriptedChat((i, _) => i < 8 ? [Call("c" + i, "hex_read", "{\"n\":" + i + "}")] : Answer("ok"));
        var tools = new FakeTools((_, _) => new AgentToolResult(big + n++, false));
        var rig = Make(chat, tools, context: 4000);
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Contains(rig.Lines, l => Type(l) == "note" && l.Contains("trimmed"));
        Assert.Contains(rig.Loop.Conversation, m => m.Content == LocalAgentLoop.DROPPED);
        Assert.Equal("system", rig.Loop.Conversation[0].Role);
    }

    [Fact]
    public async Task Old_turns_are_merged_into_a_summary_when_dropping_outputs_is_not_enough()
    {
        var rig = Make(new ScriptedChat((_, _) => Answer(new string('y', 3000))), new FakeTools(), context: 4000);
        for (int i = 0; i < 6; i++)
            await rig.Loop.RunTurnAsync("question " + i, default);

        Assert.Contains(rig.Loop.Conversation, m => m.Role == "user" && m.Content.StartsWith("Earlier in this chat (summarised):"));
        Assert.True(rig.Loop.Conversation.Count < 13);
    }

    [Theory]
    [InlineData("write", new[] { "hex_read", "hex_edit", "hex_terminal_run" })]
    [InlineData("ask", new[] { "hex_read" })]
    [InlineData("minimal", new string[0])]
    public async Task A_profile_picks_the_tools_the_model_is_given(string profile, string[] expected)
    {
        var chat = new ScriptedChat((_, _) => Answer("ok"));
        var rig = Make(chat, new FakeTools(), profile: profile);
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(expected, chat.ToolsSeen[0]);
    }

    [Fact]
    public async Task A_call_outside_the_profile_is_refused_whatever_the_approvals()
    {
        var chat = new ScriptedChat((n, _) => n == 0 ? [Call("c1", "hex_edit", "{}")] : Answer("ok"));
        var tools = new FakeTools();
        var rig = Make(chat, tools, "all", profile: "ask");
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Empty(tools.Called);
        Assert.Contains("no tool called hex_edit", rig.Loop.Conversation.Single(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task Usage_reports_the_servers_count_against_the_window()
    {
        var rig = Make(new ScriptedChat((_, _) => [new AgentDelta.Text("a"), new AgentDelta.Usage(1000, 50, 1), new AgentDelta.Done("stop")]), new FakeTools(), context: 8000);
        await rig.Loop.RunTurnAsync("x", default);

        var usage = JsonDocument.Parse(rig.Lines.Single(l => Type(l) == "usage")).RootElement;
        Assert.Equal(1000, usage.GetProperty("prompt").GetInt32());
        Assert.Equal(1050, usage.GetProperty("context").GetInt32());
        Assert.Equal(8000, usage.GetProperty("window").GetInt32());
        Assert.Equal(1050, rig.Loop.UsedTokens);
    }

    [Fact]
    public async Task A_prompt_near_the_window_is_compacted_into_a_summary_keeping_the_last_two_turns()
    {
        // Each call reports 90% of the window; the call that finds the conversation too full is asked to summarise
        var chat = new ScriptedChat((_, messages) =>
            messages[^1].Content == LocalAgentLoop.SUMMARISE
                ? Answer("SUMMARY of goals")
                : [new AgentDelta.Text("answer"), new AgentDelta.Usage(900, 0, 1), new AgentDelta.Done("stop")]);
        var rig = Make(chat, new FakeTools(), context: 1000);
        for (int i = 0; i < 4; i++)
            await rig.Loop.RunTurnAsync("q" + i, default);

        Assert.Contains(rig.Lines, l => Type(l) == "compacted");
        var conversation = rig.Loop.Conversation;
        Assert.Equal("system", conversation[0].Role);
        Assert.StartsWith("Summary of the earlier conversation", conversation[1].Content);
        Assert.Contains("SUMMARY of goals", conversation[1].Content);
        Assert.DoesNotContain(conversation, m => m.Content == "q0");
        Assert.Contains(conversation, m => m.Content == "q3");
    }

    [Fact]
    public async Task The_compact_command_summarises_on_request_and_says_so()
    {
        var chat = new ScriptedChat((_, messages) => Answer(messages[^1].Content == LocalAgentLoop.SUMMARISE ? "S" : "a"));
        var rig = Make(chat, new FakeTools());
        for (int i = 0; i < 3; i++)
            await rig.Loop.RunTurnAsync("q" + i, default);
        int calls = chat.Calls;
        rig.Lines.Clear();

        await rig.Loop.RunTurnAsync("/compact", default);

        Assert.Equal(calls + 1, chat.Calls);
        Assert.Equal(["compacted", "done"], Types(rig));
        Assert.DoesNotContain(rig.Loop.Conversation, m => m.Content == "/compact");
        Assert.DoesNotContain(rig.Loop.Conversation, m => m.Content == "q0");
    }

    [Fact]
    public async Task An_ask_answer_that_claims_an_edit_gets_a_note_that_nothing_changed()
    {
        var chat = new ScriptedChat((_, _) => Answer("I renamed total to sum in app.py."));
        var rig = Make(chat, new FakeTools(), profile: LocalAgentOptions.READ_ONLY);

        await rig.Loop.RunTurnAsync("rename total to sum", default);

        Assert.Equal(["text", "note", "done"], Types(rig).Where(t => t != "usage").ToArray());
        Assert.Contains(rig.Lines, l => Type(l) == "note" && l.Contains("No files were changed (Ask mode)"));
        Assert.Contains("can't apply it in Ask mode", rig.Loop.Conversation[0].Content);
    }

    [Fact]
    public async Task An_ask_answer_that_only_reads_gets_no_note()
    {
        var chat = new ScriptedChat((_, _) => Answer("I would rename total to sum, but I can't apply it."));
        var rig = Make(chat, new FakeTools(), profile: LocalAgentOptions.READ_ONLY);

        await rig.Loop.RunTurnAsync("rename total to sum", default);

        Assert.DoesNotContain(rig.Lines, l => Type(l) == "note");
    }

    [Fact]
    public async Task The_compact_command_with_nothing_older_says_so()
    {
        var chat = new ScriptedChat((_, _) => Answer("a"));
        var rig = Make(chat, new FakeTools());
        await rig.Loop.RunTurnAsync("/compact", default);

        Assert.Equal(0, chat.Calls);
        // A reply row of the chat's own, a note didn't show on the card
        Assert.Equal(["text", "done"], Types(rig));
        Assert.Contains(LocalAgentLoop.NOTHING_TO_COMPACT, rig.Lines[0]);
    }

    [Fact]
    public async Task A_session_is_saved_and_resumed()
    {
        var store = new LocalAgentStore(Path.Combine(folder, "sessions"));
        string session = store.NewSession();
        Assert.Matches("^local-\\d{8}-\\d{6}-[0-9a-f]{6}$", session);

        var first = Make(new ScriptedChat((n, _) => n == 0 ? [Call("c1", "hex_read", "{\"a\":1}")] : Answer("first")), new FakeTools(), store: store, session: session);
        await first.Loop.RunTurnAsync("one", default);

        var loaded = store.Load(session)!;
        Assert.Equal(first.Loop.Conversation.Count, loaded.Count);
        Assert.Equal("c1", loaded.Single(m => m.Role == "tool").CallId);
        Assert.Equal("{\"a\":1}", loaded.Single(m => m.Calls != null).Calls![0].ArgumentsJson);

        var chat = new ScriptedChat((_, _) => Answer("second"));
        var second = Make(chat, new FakeTools(), store: store, session: session);
        await second.Loop.RunTurnAsync("two", default);
        Assert.Equal("system", chat.Seen[0][0].Role);
        Assert.Contains(chat.Seen[0], m => m.Content == "one");
        Assert.Equal(loaded.Count + 2, second.Loop.Conversation.Count);
    }

    [Fact]
    public void The_store_returns_null_for_a_missing_or_corrupt_session()
    {
        var store = new LocalAgentStore(Path.Combine(folder, "s"));
        Assert.Null(store.Load("local-none"));
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(Path.Combine(store.Folder, "bad.json"), "{ nope");
        Assert.Null(store.Load("bad"));
    }

    [Fact]
    public async Task Cancelling_saves_the_conversation_and_stays_a_cancel()
    {
        var store = new LocalAgentStore(folder);
        string session = store.NewSession();
        using var source = new CancellationTokenSource();
        var chat = new ScriptedChat((_, _) => [Call("c1", "hex_read", "{}")]);
        var tools = new FakeTools((_, _) =>
        {
            source.Cancel();
            return new AgentToolResult("ok", false);
        });
        var rig = Make(chat, tools, store: store, session: session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Loop.RunTurnAsync("x", source.Token));
        var saved = store.Load(session)!;
        Assert.Contains(saved, m => m.Role == "user" && m.Content == "x");
        Assert.Contains(saved, m => m.Role == "assistant");
    }

    [Fact]
    public async Task A_failed_model_call_says_an_error_and_done()
    {
        var chat = new ScriptedChat((_, _) => throw new PredictionException(PredictionStatus.Unreachable, "no server"));
        var rig = Make(chat, new FakeTools());
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(["error", "done"], Types(rig));
        Assert.Equal("error", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Protocol_lines_have_the_panels_shapes_on_one_line()
    {
        var text = JsonDocument.Parse(LocalAgentProtocol.Text("a\nb")).RootElement;
        Assert.Equal("text", text.GetProperty("type").GetString());
        Assert.Equal("a\nb", text.GetProperty("text").GetString());
        Assert.DoesNotContain('\n', LocalAgentProtocol.Text("a\nb"));

        var tool = JsonDocument.Parse(LocalAgentProtocol.Tool("1", "hex_read", "{\"path\":\"x\"}")).RootElement;
        Assert.Equal("x", tool.GetProperty("input").GetProperty("path").GetString());
        Assert.Equal("1", tool.GetProperty("id").GetString());

        var done = JsonDocument.Parse(LocalAgentProtocol.ToolDone("1", "out", true)).RootElement;
        Assert.True(done.GetProperty("error").GetBoolean());
        Assert.Equal("out", done.GetProperty("output").GetString());

        var usage = JsonDocument.Parse(LocalAgentProtocol.Usage(5, 6, 1.5, 7, 8)).RootElement;
        Assert.Equal(5, usage.GetProperty("prompt").GetInt32());
        Assert.Equal(6, usage.GetProperty("output").GetInt32());
        Assert.Equal(1.5, usage.GetProperty("seconds").GetDouble());
        Assert.Equal(7, usage.GetProperty("context").GetInt32());
        Assert.Equal(8, usage.GetProperty("window").GetInt32());

        var approval = JsonDocument.Parse(LocalAgentProtocol.Approval("9", "hex_edit", "{}", "Run?")).RootElement;
        Assert.Equal("hex_edit", approval.GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.Object, approval.GetProperty("input").ValueKind);

        Assert.Equal("s", JsonDocument.Parse(LocalAgentProtocol.Session("s")).RootElement.GetProperty("id").GetString());
        Assert.Equal("note", Type(LocalAgentProtocol.Note("n")));
        Assert.Equal("error", Type(LocalAgentProtocol.Error("e")));
        Assert.Equal("thinking", Type(LocalAgentProtocol.Thinking("t")));
        Assert.Equal("done", Type(LocalAgentProtocol.Done("stop")));
    }

    [Fact]
    public async Task A_tool_call_written_in_the_answer_text_is_run()
    {
        var chat = new ScriptedChat((n, _) => n == 0
            ? Answer("Looking.\n<tool_call>{\"name\": \"hex_read\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call>")
            : Answer("done"));
        var tools = new FakeTools();
        var rig = Make(chat, tools);
        await rig.Loop.RunTurnAsync("look", default);

        Assert.Equal([("hex_read", "{\"path\": \"a.cs\"}")], tools.Called);
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task A_tool_call_written_in_the_reasoning_is_run()
    {
        var chat = new ScriptedChat((n, _) => n == 0
            ? [new AgentDelta.Thinking("Let me look. <tool_call>{\"name\": \"hex_read\","), new AgentDelta.Thinking(" \"arguments\": {\"path\": \"b.cs\"}}</tool_call>"), new AgentDelta.Done("stop")]
            : Answer("done"));
        var tools = new FakeTools();
        var rig = Make(chat, tools);
        await rig.Loop.RunTurnAsync("look", default);

        Assert.Equal(["hex_read"], tools.Called.Select(c => c.Name));
        Assert.Contains("b.cs", tools.Called[0].Args);
        Assert.Equal("done", rig.Loop.Conversation[^1].Content);
        Assert.Equal("done", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString() is "stop" ? "done" : "bad");
    }

    [Fact]
    public async Task An_empty_turn_is_nudged_once_and_then_answers()
    {
        var chat = new ScriptedChat((n, _) => n == 0 ? [new AgentDelta.Thinking("hmm"), new AgentDelta.Done("stop")] : Answer("here"));
        var rig = Make(chat, new FakeTools());
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(2, chat.Calls);
        Assert.Contains(rig.Loop.Conversation, m => m.Role == "user" && m.Content == LocalAgentLoop.NUDGE);
        Assert.Equal("here", rig.Loop.Conversation[^1].Content);
    }

    [Fact]
    public async Task A_turn_empty_after_the_nudge_ends_with_a_clear_message()
    {
        var chat = new ScriptedChat((_, _) => [new AgentDelta.Done("stop")]);
        var rig = Make(chat, new FakeTools());
        await rig.Loop.RunTurnAsync("x", default);

        Assert.Equal(2, chat.Calls);
        Assert.Equal(["note", "done"], Types(rig));
        Assert.Equal("empty", JsonDocument.Parse(rig.Lines[^1]).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Streamed_thinking_pieces_make_one_row()
    {
        var chat = new HexAgentChat(1, "local");
        chat.Think("a", true);
        chat.Think("b", true);
        chat.Think("c", true);
        Assert.Single(chat.Entries);
        Assert.Equal("abc", chat.Entries[0].Text);

        chat.Say("x", true);
        chat.Say("y", true);
        chat.Think("z", true);
        Assert.Equal(["abc", "xy", "z"], chat.Entries.Select(e => e.Text));

        // Without append, each is a row of its own, as for the other agent systems
        chat.Think("p");
        chat.Think("q");
        Assert.Equal(5, chat.Entries.Count);
    }

    [Fact]
    public void Input_lines_are_read()
    {
        Assert.Equal(new LocalAgentInput.Message("hi"), LocalAgentProtocol.Read("{\"type\":\"message\",\"text\":\"hi\"}"));
        Assert.Equal(new LocalAgentInput.Approval("a1", true), LocalAgentProtocol.Read("{\"type\":\"approval\",\"id\":\"a1\",\"allow\":true}"));
        Assert.Equal(new LocalAgentInput.Approval("a1", false), LocalAgentProtocol.Read("{\"type\":\"approval\",\"id\":\"a1\",\"allow\":false}"));
        Assert.IsType<LocalAgentInput.Stop>(LocalAgentProtocol.Read("{\"type\":\"stop\"}"));
        Assert.Null(LocalAgentProtocol.Read("{\"type\":\"other\"}"));
        Assert.Null(LocalAgentProtocol.Read("not json"));
        Assert.Null(LocalAgentProtocol.Read(""));
    }

    [Fact]
    public void The_system_prompt_names_the_folder_and_describes_tools_only_without_native_ones()
    {
        var tools = new FakeTools().Tools;
        string native = LocalAgentLoop.SystemPrompt("C:\\work", tools, true);
        Assert.Contains("C:\\work", native);
        Assert.Contains("hex_edit", native);
        Assert.NotEqual(native, LocalAgentLoop.SystemPrompt("C:\\work", tools, false));
    }
}
