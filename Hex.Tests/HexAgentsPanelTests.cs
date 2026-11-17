using Hex.Core;
using Hex.Dawn;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// The Agents panel without its window. A chat's rows and turns, an agent's program found and run with its lines read
/// back, JSON as HIDL and back, the rows' names and icons for tools, and the Claude Code and Codex plugins turning what
/// their agents print (recorded from the real thing's formats) into rows, and the arguments they run their agents with.
/// </summary>
[Collection(Timed.NAME)]
public sealed class HexAgentsPanelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-panel-" + Guid.NewGuid().ToString("N")[..8]);

    public HexAgentsPanelTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        HexAgentProfiles.Forget("claude_code");
        HexAgentProfiles.Forget("codex");
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    [Fact]
    public void A_chat_keeps_its_rows_and_its_tools_end_with_the_turn()
    {
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("Find where the MCP server starts\nand tell me");
        Assert.True(chat.Running);
        Assert.Equal("Find where the MCP server starts", chat.Title);

        chat.Tool("t1", "mcp__hex__hex_symbols", "{\"query\":\"StartMcp\"}");
        chat.Tool("t2", "Bash", "dotnet build");
        Assert.True(chat.ToolDone("t1", true, "Hex.Dawn/Agents/DawnScene.Mcp.cs:62 method StartMcp"));
        Assert.False(chat.ToolDone("nobody", false, "?"));
        chat.Say("It starts in **StartMcp**.");
        chat.Done();

        Assert.False(chat.Running);
        HexAgentEntry[] tools = [.. chat.Entries.Where(entry => entry.Kind == HexAgentEntryKind.Tool)];
        Assert.Equal(HexAgentToolState.Done, tools[0].State);
        Assert.Equal(HexAgentToolState.Failed, tools[1].State);
        Assert.Contains("stopped before", tools[1].Output);
        Assert.Equal(HexAgentEntryKind.Text, chat.Entries[^1].Kind);
    }

    [Fact]
    public void A_tool_id_of_a_turn_before_is_another_tool()
    {
        var chat = new HexAgentChat(1, "codex");
        chat.Ask("one");
        chat.Tool("item_1", "command", "ls");
        chat.ToolDone("item_1", true, "a");
        chat.Done();
        chat.Ask("two");
        chat.Tool("item_1", "command", "pwd");
        Assert.Equal(2, chat.Entries.Count(entry => entry.Kind == HexAgentEntryKind.Tool));
        Assert.Equal(HexAgentToolState.Running, chat.Entries[^1].State);
        Assert.True(chat.ToolDone("item_1", true, "b"));
        Assert.Equal("a", chat.Entries.First(entry => entry.Kind == HexAgentEntryKind.Tool).Output);
    }

    [Fact]
    public void Json_comes_in_as_hidl_values_and_goes_back_out()
    {
        IRuntimeValue parsed = HexJson.Parse("{\"type\":\"result\",\"n\":3,\"ok\":true,\"list\":[\"a\",null],\"inner\":{\"x\":1.5}}")!;
        var made = (ObjectValue)parsed;
        Assert.Equal("result", Values.Text(made.Properties["type"]));
        Assert.Equal(3f, ((NumberValue)made.Properties["n"]).Value);
        Assert.Equal("{\"type\":\"result\",\"n\":3,\"ok\":true,\"list\":[\"a\",null],\"inner\":{\"x\":1.5}}", HexJson.Short(parsed));
        Assert.Null(HexJson.Parse("Compiling..."));
        Assert.Null(HexJson.Parse("{ not json"));
    }

    [Fact]
    public void A_program_is_found_on_the_path_with_its_endings()
    {
        // A .cmd and PATHEXT's endings are Windows' own, elsewhere a name is found as it is
        if (!OperatingSystem.IsWindows())
            return;

        File.WriteAllText(Path.Combine(folder, "agent.cmd"), "@echo off");
        Assert.Equal(Path.Combine(folder, "agent.cmd"), HexAgentProcess.Find("agent", folder, ".EXE;.CMD"));
        Assert.Null(HexAgentProcess.Find("nobody", folder, ".EXE;.CMD"));
        Assert.Equal("plain", HexAgentProcess.CmdQuote("plain"));
        Assert.Equal("\"has space\"", HexAgentProcess.CmdQuote("has space"));
        Assert.Equal("\"say \"\"hi\"\"\"", HexAgentProcess.CmdQuote("say \"hi\""));
    }

    [Fact]
    public void A_program_run_has_its_lines_read_back_and_its_exit_said()
    {
        // A .cmd and PATHEXT's endings are Windows' own, elsewhere a name is found as it is
        if (!OperatingSystem.IsWindows())
            return;

        string script = Path.Combine(folder, "agent.cmd");
        File.WriteAllText(script, "@echo off\r\nset /p asked=\r\necho {\"type\":\"said\",\"text\":\"%asked%\"}\r\necho oops 1>&2\r\nexit /b 3\r\n");
        using HexAgentProcess run = HexAgentProcess.Start(script, ["--json"], folder, "hello there\n");
        Assert.True(SpinWait.SpinUntil(() => run.Exited, 20_000), "it never finished");
        var lines = run.Lines.ToArray();
        Assert.Contains(lines, line => !line.Error && line.Line == "{\"type\":\"said\",\"text\":\"hello there\"}");
        Assert.Contains(lines, line => line.Error && line.Line.Trim() == "oops");
        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public void Tools_are_named_and_given_icons_by_what_they_do()
    {
        Assert.Equal("hex_search", HexAgentRows.ToolTitle("mcp__hex__hex_search"));
        Assert.Equal("Bash", HexAgentRows.ToolTitle("Bash"));
        Assert.Equal("search", HexAgentRows.ToolIcon("mcp__hex__hex_search"));
        Assert.Equal("terminal", HexAgentRows.ToolIcon("command"));
        Assert.Equal("diff", HexAgentRows.ToolIcon("Edit"));
        Assert.Equal("notebook", HexAgentRows.ToolIcon("mcp__hex__hex_notebook_run"));
        Assert.Equal("tools", HexAgentRows.ToolIcon("something_else"));
        Assert.Equal("a b c", HexAgentRows.OneLine("a\n  b\tc", 40));
        Assert.Equal("abcdefg...", HexAgentRows.OneLine("abcdefghijklmnop", 10));
    }

    [Theory]
    [InlineData("{\"cmd\":\"dotnet build\",\"yield_time_ms\":1000}", "dotnet build")]
    [InlineData("{\"path\":\"src/main.cs\"}", "src/main.cs")]
    [InlineData("{\"query\":\"StartMcp\"}", "StartMcp")]
    [InlineData("dotnet test --no-build", "dotnet test --no-build")]
    [InlineData("{ partial", "{ partial")]
    [InlineData("{\"recursive\":true}", "recursive")]
    [InlineData(null, "")]
    public void Tool_previews_show_useful_arguments_and_tolerate_raw_input(string? input, string expected) =>
        Assert.Equal(expected, HexAgentRows.ToolPreview(input));

    [Fact]
    public void Tool_labels_fit_even_when_the_panel_is_narrow()
    {
        Assert.Equal("..", HexAgentRows.OneLine("long input", 2));
        Assert.Equal(".", HexAgentRows.OneLine("long input", 0));
    }

    /// <summary>A plugin of the catalog loaded against a hex with what an agent system uses, and the run it said.</summary>
    private (HIDLRuntime Runtime, ObjectValue Hex, ObjectValue Run) Load(string id, Dictionary<string, IRuntimeValue>? settings = null)
    {
        ObjectValue? run = null;
        ObjectValue hex = Natives.Object(
            ("agent", Natives.Action("agent", args =>
            {
                ObjectValue said = args.Object(0);
                run = said.Properties.TryGetValue("run", out IRuntimeValue? running) ? (ObjectValue)running : null;
            })),
            ("setting", Natives.Function("setting", args => settings?.GetValueOrDefault(args.Text(0)) ?? args.Text(0) switch
            {
                "budget" => new NumberValue(8000),
                "permission_mode" => new StringValue("acceptEdits"),
                "allowed_tools" => new StringValue("mcp__hex Read"),
                "sandbox" => new StringValue("workspace-write"),
                "program" => new StringValue(id == "codex" ? "codex" : "claude"),
                _ => new StringValue(string.Empty)
            })),
            ("temp", new StringValue(folder)),
            ("harness_rules", new StringValue(HexAgentProfiles.HARNESS_RULES)),
            ("harness_check", Natives.Function("harness_check", args =>
                HexHarnessGuard.ForCommand(args.Text(0, string.Empty), [folder]) is { } instead ? new StringValue(instead) : Values.Null)),
            ("home", new StringValue(folder)),
            ("exists", Natives.Function("exists", args => Values.Bool(File.Exists(args.Text(0)) || Directory.Exists(args.Text(0))))),
            ("read", Natives.Function("read", args => new StringValue(File.ReadAllText(args.Text(0))))),
            ("json_parse", Natives.Function("json_parse", args => HexJson.Parse(args.Text(0)) ?? Values.Null)),
            ("mcp_server", Natives.Function("mcp_server", args => Natives.Object(("command", new StringValue(@"C:\Hex\Hex.exe")), ("args", Natives.List(["--mcp-connect", "--folder", @"C:\work",
                .. args.Has(0) && args[0] is ObjectValue chat ? new[] { "--agent-chat", Values.Text(chat.Properties["id"]) } : []]))))),
            ("json_set", Natives.Function("json_set", args =>
                HexExtensions.JsonSet(args.Text(0), [.. ((ListValue)args[1]).Items.Select(Values.Text)], args[2]) is { } why ? new StringValue(why) : Values.Null)),
            ("command", Natives.Action("command", _ => { })),
            ("say", Natives.Action("say", _ => { })));

        string extension = Path.Combine(Catalog.Folder, id);
        var runtime = new HIDLRuntime { BaseDirectory = extension, Output = null };
        runtime.UserScope.DeclareSystem("hex", hex);
        runtime.RunFile(Path.Combine(extension, "extension.hor"));
        runtime.Invoke(runtime.UserScope.Variables["on_start"], hex);
        Assert.NotNull(run);
        return (runtime, hex, run!.Value);
    }

    private static void Feed(HIDLRuntime runtime, ObjectValue run, ObjectValue chat, params string[] lines)
    {
        IRuntimeValue heard = run.Properties["on_event"];
        foreach (string line in lines)
            runtime.Invoke(heard, chat, HexJson.Parse(line)!);
    }

    private static string[] Arguments(HIDLRuntime runtime, ObjectValue run, ObjectValue chat) =>
        [.. ((ListValue)runtime.Invoke(run.Properties["arguments"], new StringValue("hi"), chat)).Items.Select(Values.Text)];

    // What a run's message writes on the agent's stdin for something asked, as JSON
    private static string Said(HIDLRuntime runtime, ObjectValue run, ObjectValue chat, string text) =>
        HexJson.FromHidl(runtime.Invoke(run.Properties["message"], new StringValue(text), chat))!.ToJsonString();

    [Fact]
    public void Pasted_pictures_go_to_claude_as_image_blocks_and_to_codex_as_files()
    {
        Directory.CreateDirectory(folder);
        string picture = Path.Combine(folder, "pasted.png");
        File.WriteAllBytes(picture, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);

        var (claude, _, claudeRun) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code") { Attached = [picture] };
        ObjectValue api = HexAgentChatApi.Of(chat, folder);
        string said = HexJson.FromHidl(claude.Invoke(claudeRun.Properties["message"], new StringValue("what is this"), api))!.ToJsonString();
        Assert.Contains("\"type\":\"image\"", said);
        Assert.Contains("\"media_type\":\"image/png\"", said);
        Assert.Contains($"\"data\":\"{Convert.ToBase64String(File.ReadAllBytes(picture))}\"", said);
        Assert.True(said.IndexOf("\"type\":\"image\"", StringComparison.Ordinal) < said.IndexOf("what is this", StringComparison.Ordinal), "the picture comes before the words");

        // Nothing pasted, the message is the words alone as it was
        chat.Attached = [];
        Assert.DoesNotContain("image", HexJson.FromHidl(claude.Invoke(claudeRun.Properties["message"], new StringValue("plain"), api))!.ToJsonString());

        var (codex, _, codexRun) = Load("codex");
        var other = new HexAgentChat(2, "codex") { Attached = [picture] };
        var written = new List<string>();
        ObjectValue otherApi = HexAgentChatApi.Of(other, folder, write: line => { written.Add(line); return true; });
        Arguments(codex, codexRun, otherApi);
        Said(codex, codexRun, otherApi, "what is this");
        Feed(codex, codexRun, otherApi, """{"id":2,"result":{"thread":{"id":"t-2"}}}""");
        // Its turn takes the picture by its file
        Assert.Contains(written, line => line.Contains("\"type\":\"localImage\"", StringComparison.Ordinal)
            && line.Contains(HexJson.FromHidl(new StringValue(picture))!.ToJsonString().Trim('"'), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Claude Opus 5.5")]
    [InlineData("claude-sonnet-4-5-20250929", "Claude Sonnet 4.5")]
    [InlineData("claude-3-5-haiku-20241022", "Claude Haiku 3.5")]
    [InlineData("sonnet[1m]", "Claude Sonnet (1M context)")]
    [InlineData("opus", "Claude Opus")]
    [InlineData("gpt-5.1-codex-max", "GPT-5.1-Codex-Max")]
    [InlineData("gemini-2.5-pro", "Gemini 2.5 Pro")]
    [InlineData("o3", "o3")]
    [InlineData("", null)]
    public void Models_are_said_in_full(string id, string? expected) => Assert.Equal(expected, HexAgentModels.Name(id));

    [Fact]
    public void Claude_code_is_asked_its_models_as_a_run_starts_and_the_picker_has_them()
    {
        ObjectValue? said = null;
        var (runtime, hex, run) = Load("claude_code");
        hex.Properties["agent"] = Natives.Action("agent", args => said = args.Object(0));
        var chat = new HexAgentChat(1, "claude_code");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        Arguments(runtime, run, api);
        string first = Said(runtime, run, api, "hi");
        Assert.StartsWith("[", first);
        Assert.Contains("\"subtype\":\"initialize\"", first);
        Assert.Contains("\"type\":\"user\"", first);
        // Only the first message of a run goes with it
        Assert.DoesNotContain("initialize", Said(runtime, run, api, "more"));
        // Stop interrupts its turn rather than killing it
        Assert.Contains("\"subtype\":\"interrupt\"", HexJson.FromHidl(runtime.Invoke(run.Properties["stop"], api))!.ToJsonString());

        Feed(runtime, run, api, """{"type":"control_response","response":{"subtype":"success","request_id":"hex-init-1","response":{"commands":[{"name":"compact","description":"Compact"}],"models":[{"value":"default","displayName":"Default (recommended)","description":"Opus 5.5"},{"value":"sonnet","displayName":"Sonnet","description":"Sonnet 5.5"}]}}}""");
        Assert.NotNull(said);
        string options = HexJson.FromHidl(((ObjectValue)said!.Value.Properties["run"]).Properties["options"])!.ToJsonString();
        Assert.Contains("\"label\":\"Default (recommended)\"", options);
        Assert.Contains("\"value\":\"sonnet\"", options);
        Assert.DoesNotContain("Haiku", options);
    }

    [Fact]
    public void A_chat_says_the_model_it_runs_over_the_alias_it_was_asked_for()
    {
        var chat = new HexAgentChat(1, "claude_code");
        chat.Options["model"] = "opus";
        Assert.Equal("Claude Opus", HexAgentModels.Of(chat));
        chat.Model = "claude-opus-5-5";
        Assert.Equal("Claude Opus 5.5", HexAgentModels.Of(chat));
        HexAgentModels.Said("gpt-test-only", "Test Model");
        Assert.Equal("Test Model", HexAgentModels.Name("gpt-test-only"));
    }

    [Fact]
    public void A_question_waits_on_the_person_once_and_says_who_answered()
    {
        var chat = new HexAgentChat(1, "claude_code");
        var heard = new List<string?>();
        HexAgentEntry asked = chat.Question("Which way?", ["Rewrite it", "Patch it"], 1, 300, heard.Add);
        Assert.Equal(1, asked.Recommended);
        Assert.Same(asked, Assert.Single(chat.Pending));

        Assert.True(chat.Reply(asked, "Patch it, but keep the test"));
        Assert.False(chat.Reply(asked, "again"));
        Assert.Equal(["Patch it, but keep the test"], heard);
        Assert.Equal(HexAgentToolState.Done, asked.State);
        Assert.Empty(chat.Pending);

        // Nobody answering is an answer of null, the row saying so
        HexAgentEntry unanswered = chat.Question("And then?", ["A", "B"], 9, 300, heard.Add);
        Assert.Null(unanswered.Recommended);
        Assert.True(chat.Reply(unanswered, null));
        Assert.Equal(HexAgentToolState.Failed, unanswered.State);
        Assert.Null(heard[^1]);
    }

    [Fact]
    public void Claude_codes_leave_asked_is_turned_back_to_hex_or_asked_of_the_person()
    {
        Directory.CreateDirectory(folder);
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        var written = new List<string>();
        ObjectValue api = HexAgentChatApi.Of(chat, folder, write: line => { written.Add(line); return true; },
            call: (function, args) => { runtime.Invoke(function, args); return true; });
        Assert.Contains("--permission-prompt-tool", Arguments(runtime, run, api));

        // A search of the workspace by the shell is refused at once with the Hex tool, nobody asked
        Feed(runtime, run, api, """{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"grep -rn foo ."}}}""");
        Assert.Empty(chat.Pending);
        Assert.Contains(written, line => line.Contains("\"request_id\":\"r1\"", StringComparison.Ordinal)
            && line.Contains("\"behavior\":\"deny\"", StringComparison.Ordinal) && line.Contains("hex_", StringComparison.Ordinal));

        // Anything else waits on a card, and its answer goes back with the input as it was
        Feed(runtime, run, api, """{"type":"control_request","request_id":"r2","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"dotnet test"}}}""");
        HexAgentEntry asked = Assert.Single(chat.Pending);
        Assert.Equal("dotnet test", asked.Input);
        Assert.DoesNotContain(written, line => line.Contains("\"request_id\":\"r2\"", StringComparison.Ordinal));
        Assert.True(chat.Decide(asked, true));
        Assert.Contains(written, line => line.Contains("\"request_id\":\"r2\"", StringComparison.Ordinal)
            && line.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal) && line.Contains("dotnet test", StringComparison.Ordinal));

        // Its own questions are cards, its answers going back in the input by each question, its recommended one marked
        Feed(runtime, run, api, """{"type":"control_request","request_id":"q1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which way?","header":"Way","multiSelect":false,"options":[{"label":"Rewrite (Recommended)","description":"a"},{"label":"Patch","description":"b"}]},{"question":"Tests?","header":"Tests","multiSelect":false,"options":[{"label":"Yes","description":"c"},{"label":"No","description":"d"}]}]}}}""");
        HexAgentEntry[] cards = [.. chat.Pending];
        Assert.Equal(2, cards.Length);
        Assert.Equal(0, cards[0].Recommended);
        Assert.True(chat.Reply(cards[0], "Patch"));
        Assert.DoesNotContain(written, line => line.Contains("\"request_id\":\"q1\"", StringComparison.Ordinal));
        Assert.True(chat.Reply(cards[1], "Only the fast ones"));
        Assert.Contains(written, line => line.Contains("\"request_id\":\"q1\"", StringComparison.Ordinal) && line.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal)
            && line.Contains("\"answers\":{\"Which way?\":\"Patch\",\"Tests?\":\"Only the fast ones\"}", StringComparison.Ordinal));

        // Nobody answering refuses the tool with word to go on, the other card taken back
        Feed(runtime, run, api, """{"type":"control_request","request_id":"q2","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"A?","options":[{"label":"x"}]},{"question":"B?","options":[{"label":"y"}]}]}}}""");
        cards = [.. chat.Pending];
        Assert.True(chat.Reply(cards[0], null));
        Assert.Empty(chat.Pending);
        Assert.Contains(written, line => line.Contains("\"request_id\":\"q2\"", StringComparison.Ordinal) && line.Contains("\"behavior\":\"deny\"", StringComparison.Ordinal)
            && line.Contains("judge best", StringComparison.Ordinal));

        // One it gives up asking is taken back unanswered
        Feed(runtime, run, api, """{"type":"control_request","request_id":"r3","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"a.txt"}}}""",
            """{"type":"control_cancel_request","request_id":"r3"}""");
        Assert.Empty(chat.Pending);
        Assert.DoesNotContain(written, line => line.Contains("\"request_id\":\"r3\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_codes_stream_json_becomes_rows_a_session_and_what_the_turn_used()
    {
        var (runtime, _, run) = Load("claude_code");
        Assert.Equal("claude", HexExtensions.Text(run, "program"));
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("where does the server start");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        Feed(runtime, run, api,
            """{"type":"system","subtype":"init","session_id":"5f1c2d3e-aaaa","model":"claude-sonnet-5-5","tools":["mcp__hex__hex_symbols"]}""",
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Look it up in the index."},{"type":"tool_use","id":"toolu_1","name":"mcp__hex__hex_symbols","input":{"query":"StartMcp"}}]},"session_id":"5f1c2d3e-aaaa"}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"Hex.Dawn/Agents/DawnScene.Mcp.cs:62 method StartMcp"}]}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"It starts in `StartMcp`."}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"It starts in `StartMcp`.","session_id":"5f1c2d3e-aaaa","total_cost_usd":0.0123,"usage":{"input_tokens":1200,"cache_read_input_tokens":800,"output_tokens":90}}""");

        Assert.Equal("5f1c2d3e-aaaa", chat.Session);
        Assert.Equal("claude-sonnet-5-5", chat.Model);
        Assert.Equal(2000, chat.InputTokens);
        Assert.Equal(90, chat.OutputTokens);
        Assert.Equal(0.0123, chat.Cost, 4);
        Assert.False(chat.Running);

        HexAgentEntry tool = chat.Entries.Single(entry => entry.Kind == HexAgentEntryKind.Tool);
        Assert.Equal("mcp__hex__hex_symbols", tool.ToolName);
        Assert.Equal("{\"query\":\"StartMcp\"}", tool.Input);
        Assert.Equal(HexAgentToolState.Done, tool.State);
        Assert.Contains("DawnScene.Mcp.cs:62", tool.Output);
        Assert.Contains(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Thinking && entry.Text == "Look it up in the index.");
        Assert.Equal("It starts in `StartMcp`.", chat.Entries.Last(entry => entry.Kind == HexAgentEntryKind.Text).Text);
    }

    [Fact]
    public void A_claude_subagents_steps_go_on_the_row_of_the_task_that_started_it()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("find where the server starts");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        Feed(runtime, run, api,
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_task","name":"Task","input":{"description":"Find the server start","prompt":"Look everywhere for where the MCP server starts and say the file and line.","subagent_type":"Explore"}}]}}""",
            """{"type":"assistant","parent_tool_use_id":"toolu_task","message":{"content":[{"type":"text","text":"Looking it up.\nThen reading it."},{"type":"tool_use","id":"toolu_s1","name":"mcp__hex__hex_symbols","input":{"query":"StartMcp"}}]}}""",
            """{"type":"user","parent_tool_use_id":"toolu_task","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_s1","content":"Hex.Dawn/Agents/DawnScene.Mcp.cs:62"}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_task","content":"DawnScene.Mcp.cs:62"}]}}""");

        HexAgentEntry task = Assert.Single(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Tool);
        Assert.Equal(2, task.Steps);
        Assert.Equal("hex_symbols StartMcp", task.Progress);
        Assert.Equal(HexAgentToolState.Done, task.State);
        Assert.DoesNotContain(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Text);
        Assert.Equal(("Subagent · Explore", "Find the server start"), HexAgentRows.SubagentOf(task));
        Assert.Null(HexAgentRows.SubagentOf(new HexAgentEntry(HexAgentEntryKind.Tool, "Read") { ToolName = "Read", Input = "{}" }));
    }

    [Fact]
    public void A_chats_messages_stay_unread_until_read_and_reading_only_goes_forward()
    {
        var chat = new HexAgentChat(2, "codex");
        int version = chat.Version;
        chat.Post(new HexAgentMail(1, 1, "#1", "finding", "the server starts in StartMcp", false));
        chat.Post(new HexAgentMail(4, 3, "#3", "instruction", "test it next", true));
        Assert.True(chat.Version > version);
        Assert.Equal([1L, 4L], chat.UnreadMail().Select(message => message.Seq));

        chat.MailRead(1);
        Assert.Equal(4, Assert.Single(chat.UnreadMail()).Seq);
        chat.MailRead(0);
        Assert.Equal(1, chat.MailSeen);
        chat.MailRead(4);
        Assert.Empty(chat.UnreadMail());
    }

    [Fact]
    public void A_note_draws_the_icon_it_starts_with_and_nothing_else_of_its_markup()
    {
        Assert.Equal("$(mail) " + Horizon.UI.Drawing.UIRichText.Literal("from #3 · finding: a [tag]"), HexAgentRows.NoteText("$(mail) from #3 · finding: a [tag]"));
        Assert.Equal(Horizon.UI.Drawing.UIRichText.Literal("plain [tag]"), HexAgentRows.NoteText("plain [tag]"));
        Assert.Equal(Horizon.UI.Drawing.UIRichText.Literal("$(not an icon) x"), HexAgentRows.NoteText("$(not an icon) x"));
    }

    [Fact]
    public void Claude_code_runs_with_hex_as_its_server_and_resumes_its_session()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        string[] first = Arguments(runtime, run, api);
        Assert.EndsWith("claude-mcp-1.json", first[8]);
        Assert.Equal(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--replay-user-messages", "--verbose", "--mcp-config", first[8], "--permission-mode", "acceptEdits",
            "--permission-prompt-tool", "stdio", "--append-system-prompt", HexAgentProfiles.HARNESS_RULES, "--agents", first[16], "--allowedTools", "mcp__hex Read"], first);
        Assert.EndsWith("claude-agents-1.json", first[16]);
        using (System.Text.Json.JsonDocument agents = System.Text.Json.JsonDocument.Parse(File.ReadAllText(first[16])))
            Assert.EndsWith(HexAgentProfiles.HARNESS_RULES, agents.RootElement.GetProperty("Explore").GetProperty("prompt").GetString());
        string config = File.ReadAllText(first[8]);
        Assert.Contains("--mcp-connect", config);
        Assert.Contains("\"--agent-chat\"", config);
        Assert.Contains(@"C:\\Hex\\Hex.exe", config);

        chat.Session = "abc";
        Assert.Equal(["--resume", "abc"], Arguments(runtime, run, api)[^2..]);
    }

    [Fact]
    public void Something_asked_as_the_agent_works_is_queued_until_taken_and_unheard_when_the_turn_ends_first()
    {
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("first");
        HexAgentEntry taken = chat.Steer("also this");
        HexAgentEntry left = chat.Steer("and this");
        Assert.True(chat.Running);
        Assert.True(taken.Queued && left.Queued && chat.HasQueued);

        Assert.True(chat.Taken());
        Assert.False(taken.Queued);
        Assert.True(left.Queued);

        chat.Done();
        Assert.False(chat.HasQueued);
        Assert.False(taken.Unheard);
        Assert.True(left.Unheard);
        Assert.False(chat.Taken());
    }

    [Fact]
    public void Claude_code_is_steered_in_stream_json_and_says_what_it_took_in()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("go");
        chat.Steer("and BANANA");
        int taken = 0, finished = 0;
        ObjectValue api = HexAgentChatApi.Of(chat, folder, finished: () => finished++, taken: () => taken++);

        IRuntimeValue said = runtime.Invoke(run.Properties["message"], new StringValue("and BANANA"), api);
        Assert.Equal("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"and BANANA"}]}}""", HexJson.FromHidl(said)!.ToJsonString());

        Feed(runtime, run, api,
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"go"}]},"session_id":"s","isReplay":true}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"one","is_error":false}]},"session_id":"s"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"and BANANA"}]},"session_id":"s","isReplay":true}""",
            """{"type":"result","subtype":"success","is_error":false,"session_id":"s","usage":{}}""");
        Assert.Equal(2, taken);
        Assert.Equal(1, finished);
    }

    [Fact]
    public void A_program_kept_open_is_told_more_until_its_input_is_closed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string script = Path.Combine(folder, "steered.cmd");
        File.WriteAllText(script, "@echo off\r\nset /p first=\r\necho got %first%\r\nset /p second=\r\necho got %second%\r\n");
        using HexAgentProcess run = HexAgentProcess.Start(script, [], folder, "one\n", keepInput: true);
        Assert.True(run.TakesInput);
        Assert.True(SpinWait.SpinUntil(() => run.Lines.Any(line => line.Line.Trim() == "got one"), 20_000), "the first never came back");
        Assert.True(run.Send("two"));
        run.CloseInput();
        Assert.False(run.TakesInput);
        Assert.False(run.Send("three"));
        Assert.True(SpinWait.SpinUntil(() => run.Exited, 20_000), "it never finished");
        Assert.Contains(run.Lines.ToArray(), line => line.Line.Trim() == "got two");
    }

    [Fact]
    public void A_chat_started_with_a_model_runs_with_it_over_the_setting()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(4, "claude_code") { Parent = 1, Depth = 1, Name = "scout" };
        chat.Options["model"] = "haiku";
        string[] said = Arguments(runtime, run, HexAgentChatApi.Of(chat, folder));
        Assert.Equal("haiku", said[Array.IndexOf(said, "--model") + 1]);
        Assert.EndsWith("claude-mcp-4.json", said[8]);
        Assert.Contains("\"4\"", File.ReadAllText(said[8]));
        Assert.Equal("scout", chat.Shown);

        var (codex, _, codexRun) = Load("codex");
        var other = new HexAgentChat(5, "codex");
        other.Options["model"] = "gpt-5";
        ObjectValue otherApi = HexAgentChatApi.Of(other, folder);
        string[] codexSaid = [.. ((ListValue)codex.Invoke(codexRun.Properties["arguments"], new StringValue("hi"), otherApi)).Items.Select(Values.Text)];
        Assert.Contains(codexSaid, each => each.Contains("\"--agent-chat\",\"5\""));
        // The model goes with the thread it starts
        Assert.Contains("\"model\":\"gpt-5\"", Said(codex, codexRun, otherApi, "hi"));
    }

    [Fact]
    public void A_chats_last_reply_is_what_it_said_since_it_was_last_asked_and_its_unread_until_seen()
    {
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("first");
        chat.Say("old");
        chat.Done();
        chat.Ask("second");
        chat.Tool("t", "Bash", "{}");
        chat.Say("found it");
        chat.Say("in Program.cs");
        Assert.False(chat.Unread);
        chat.Done();
        Assert.Equal("found it\n\nin Program.cs", chat.LastReply());
        Assert.Equal("…Program.cs", chat.LastReply(10));
        Assert.True(chat.Unread);
        Assert.False(chat.Failed);
        chat.Seen = chat.Version;
        Assert.False(chat.Unread);

        chat.Ask("third");
        chat.Error("broke");
        chat.Done();
        Assert.True(chat.Failed);
    }

    [Fact]
    public void A_bridge_tags_itself_with_its_chat_at_the_door()
    {
        var wire = new MemoryStream();
        var answer = new MemoryStream("OK\n"u8.ToArray());
        Assert.True(McpGate.Knock(new Duplex(answer, wire), "abc123", "7", out _));
        Assert.Equal("HEX-MCP abc123 7\n", System.Text.Encoding.UTF8.GetString(wire.ToArray()));

        var output = new MemoryStream();
        Assert.True(McpGate.Admit(new StringReader("HEX-MCP abc123 7\n"), output, "abc123", out string? tag));
        Assert.Equal("7", tag);
        Assert.True(McpGate.Admit(new StringReader("HEX-MCP abc123\n"), output, "abc123", out tag));
        Assert.Null(tag);
        Assert.False(McpGate.Admit(new StringReader("HEX-MCP wrong 7\n"), output, "abc123", out tag));
        Assert.Null(tag);
    }

    // Reads what's said to it from one stream and writes to another, a pipe's two ends
    private sealed class Duplex(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
    }

    [Fact]
    public void A_failed_claude_turn_says_why()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("go");
        Feed(runtime, run, HexAgentChatApi.Of(chat, folder),
            """{"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s","usage":{}}""");
        Assert.Equal("error_max_turns", chat.Entries.Single(entry => entry.Kind == HexAgentEntryKind.Error).Text);
        Assert.False(chat.Running);
    }

    [Fact]
    public void Codex_speaks_its_app_server_its_items_rows_its_thread_the_session_and_its_limits_and_tokens_said()
    {
        var (runtime, _, run) = Load("codex");
        Assert.Equal("codex", HexExtensions.Text(run, "program"));
        var chat = new HexAgentChat(1, "codex");
        chat.Ask("run the tests");
        var written = new List<string>();
        ObjectValue api = HexAgentChatApi.Of(chat, folder, write: line => { written.Add(line); return true; });

        // A turn starts with the handshake and the thread, the turn itself once the thread's ready
        Arguments(runtime, run, api);
        string start = Said(runtime, run, api, "run the tests");
        Assert.Contains("\"method\":\"initialize\"", start);
        Assert.Contains("\"method\":\"thread/start\"", start);
        Assert.Contains("\"approvalPolicy\":\"untrusted\"", start);
        Feed(runtime, run, api, """{"id":2,"result":{"thread":{"id":"t-1"}}}""");
        Assert.Equal("t-1", chat.Session);
        Assert.Contains(written, line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal) && line.Contains("\"threadId\":\"t-1\"", StringComparison.Ordinal)
            && line.Contains("run the tests", StringComparison.Ordinal));

        // Asked while it works, it's steered into the turn
        Feed(runtime, run, api, """{"id":3,"result":{"turn":{"id":"turn-1","status":"inProgress"}}}""");
        string steer = Said(runtime, run, api, "and the docs");
        Assert.Contains("\"method\":\"turn/steer\"", steer);
        Assert.Contains("\"expectedTurnId\":\"turn-1\"", steer);

        // A read of the workspace by the shell is declined, so it goes to Hex's tools, anything else let through
        Feed(runtime, run, api,
            """{"method":"item/commandExecution/requestApproval","id":7,"params":{"command":"\"C:\\\\Windows\\\\powershell.exe\" -Command 'grep -rn foo .'","cwd":"C:\\ws"}}""",
            """{"method":"item/commandExecution/requestApproval","id":8,"params":{"command":"\"C:\\\\Windows\\\\powershell.exe\" -Command 'dotnet test'","cwd":"C:\\ws"}}""");
        Assert.Contains(written, line => line.Contains("\"id\":7", StringComparison.Ordinal) && line.Contains("\"decline\"", StringComparison.Ordinal));
        Assert.Contains(written, line => line.Contains("\"id\":8", StringComparison.Ordinal) && line.Contains("\"accept\"", StringComparison.Ordinal));

        Feed(runtime, run, api,
            """{"method":"item/started","params":{"item":{"type":"userMessage","id":"u0"}}}""",
            """{"method":"item/started","params":{"item":{"type":"commandExecution","id":"c1","command":"\"C:\\\\Windows\\\\powershell.exe\" -Command 'dotnet test'","status":"inProgress"}}}""",
            """{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"c1","status":"failed","exitCode":1,"aggregatedOutput":"Failed: 1, Passed: 41"}}}""",
            """{"method":"item/started","params":{"item":{"type":"mcpToolCall","id":"m1","server":"hex","tool":"hex_read","arguments":{"symbol":"Check"},"status":"inProgress"}}}""",
            """{"method":"item/completed","params":{"item":{"type":"mcpToolCall","id":"m1","server":"hex","tool":"hex_read","status":"completed","result":{"content":"McpSchema.cs:43-88"}}}}""",
            """{"method":"item/started","params":{"item":{"type":"fileChange","id":"f1","changes":[{"path":"Hex.Core/Mcp/McpSchema.cs","kind":"update"}],"status":"inProgress"}}}""",
            """{"method":"item/completed","params":{"item":{"type":"fileChange","id":"f1","changes":[{"path":"Hex.Core/Mcp/McpSchema.cs","kind":"update"}],"status":"completed"}}}""",
            """{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"a1","text":"One test failed, fixed it."}}}""",
            """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":24763,"cachedInputTokens":24448,"outputTokens":122},"last":{"inputTokens":24763},"modelContextWindow":258400}}}""",
            """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":30000,"cachedInputTokens":24448,"outputTokens":200},"last":{"inputTokens":30000},"modelContextWindow":258400}}}""",
            """{"method":"account/rateLimits/updated","params":{"rateLimits":{"primary":{"usedPercent":15,"windowDurationMins":300,"resetsAt":1794951000},"secondary":{"usedPercent":35,"windowDurationMins":10080,"resetsAt":1795341600}}}}""",
            """{"method":"thread/compacted","params":{"threadId":"t-1","turnId":"turn-1"}}""",
            """{"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"completed"}}}""");

        // The tokens counted once, each count adding only what's new
        Assert.Equal(30000, chat.InputTokens);
        Assert.Equal(200, chat.OutputTokens);
        Assert.Equal(258400, chat.ContextLimit);
        Assert.False(chat.Running);

        HexAgentEntry[] tools = [.. chat.Entries.Where(entry => entry.Kind == HexAgentEntryKind.Tool)];
        Assert.Equal(["command", "mcp__hex__hex_read", "file_change"], tools.Select(tool => tool.ToolName));
        Assert.Equal("dotnet test", tools[0].Input);
        Assert.Equal(HexAgentToolState.Failed, tools[0].State);
        Assert.Equal(HexAgentToolState.Done, tools[1].State);
        Assert.Equal("One test failed, fixed it.", chat.Entries.Last(entry => entry.Kind == HexAgentEntryKind.Text).Text);

        // Both of its windows, and the compaction
        Assert.Equal(["five_hour", "seven_day"], chat.Limits.Keys.Order().ToArray());
        Assert.Equal(0.35, chat.Limits["seven_day"].Used, 3);
        Assert.Equal(1, chat.Compactions);
    }

    [Fact]
    public void Codex_runs_its_app_server_with_hex_given_as_config_and_takes_its_thread_up_again()
    {
        var (runtime, _, run) = Load("codex");
        var chat = new HexAgentChat(1, "codex");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        Assert.Equal(["app-server",
            "-c", "mcp_servers.hex.command=\"C:/Hex/Hex.exe\"",
            "-c", "mcp_servers.hex.args=[\"--mcp-connect\",\"--folder\",\"C:/work\",\"--agent-chat\",\"1\"]",
            "-c", "mcp_servers.hex.tool_timeout_sec=660", "-c", "mcp_servers.hex.enabled=true",
            "-c", "mcp_servers.hex.default_tools_approval_mode=\"approve\""], Arguments(runtime, run, api));
        Assert.Contains(HexAgentProfiles.HARNESS_RULES.Replace("\n", "\\n").Replace("\"", "\\\"")[..40], Said(runtime, run, api, "hi"));

        // A chat that's been in a thread takes it up again
        chat.Session = "thread-1";
        Arguments(runtime, run, api);
        string again = Said(runtime, run, api, "hi");
        Assert.Contains("\"method\":\"thread/resume\"", again);
        Assert.Contains("\"threadId\":\"thread-1\"", again);
    }
}
