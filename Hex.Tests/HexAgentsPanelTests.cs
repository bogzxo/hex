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
        Assert.Equal("hex_search", DawnScene.ToolTitle("mcp__hex__hex_search"));
        Assert.Equal("Bash", DawnScene.ToolTitle("Bash"));
        Assert.Equal("search", DawnScene.ToolIcon("mcp__hex__hex_search"));
        Assert.Equal("terminal", DawnScene.ToolIcon("command"));
        Assert.Equal("diff", DawnScene.ToolIcon("Edit"));
        Assert.Equal("notebook", DawnScene.ToolIcon("mcp__hex__hex_notebook_run"));
        Assert.Equal("tools", DawnScene.ToolIcon("something_else"));
        Assert.Equal("a b c", DawnScene.OneLine("a\n  b\tc", 40));
        Assert.Equal("abcdefg...", DawnScene.OneLine("abcdefghijklmnop", 10));
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
            ("mcp_server", Natives.Function("mcp_server", _ => Natives.Object(("command", new StringValue(@"C:\Hex\Hex.exe")), ("args", Natives.List(["--mcp-connect", "--folder", @"C:\work"]))))),
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
    public void Claude_code_runs_with_hex_as_its_server_and_resumes_its_session()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        string[] first = Arguments(runtime, run, api);
        Assert.EndsWith("claude-mcp.json", first[5]);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--mcp-config", first[5], "--permission-mode", "acceptEdits", "--allowedTools", "mcp__hex Read"], first);
        string config = File.ReadAllText(first[5]);
        Assert.Contains("--mcp-connect", config);
        Assert.Contains(@"C:\\Hex\\Hex.exe", config);

        chat.Session = "abc";
        Assert.Equal(["--resume", "abc"], Arguments(runtime, run, api)[^2..]);
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
    public void Codex_items_become_rows_and_its_thread_the_session()
    {
        var (runtime, _, run) = Load("codex");
        Assert.Equal("codex", HexExtensions.Text(run, "program"));
        var chat = new HexAgentChat(1, "codex");
        chat.Ask("run the tests");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        Feed(runtime, run, api,
            """{"type":"thread.started","thread_id":"0199a213-81c0-7800-8aa1-bbab2a035a53"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"reasoning","text":"**Running the tests**"}}""",
            """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"dotnet test","aggregated_output":"","status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"dotnet test","aggregated_output":"Failed: 1, Passed: 41","exit_code":1,"status":"failed"}}""",
            """{"type":"item.started","item":{"id":"item_2","type":"mcp_tool_call","server":"hex","tool":"hex_read","arguments":{"symbol":"Check"},"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"mcp_tool_call","server":"hex","tool":"hex_read","status":"completed","result":"McpSchema.cs:43-88"}}""",
            """{"type":"item.completed","item":{"id":"item_3","type":"file_change","changes":[{"path":"Hex.Core/Mcp/McpSchema.cs","kind":"update"}],"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"item_4","type":"todo_list","items":[{"text":"fix it","completed":true},{"text":"test again","completed":false}]}}""",
            """{"type":"item.completed","item":{"id":"item_5","type":"agent_message","text":"One test failed, fixed it."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":24763,"cached_input_tokens":24448,"output_tokens":122}}""");

        Assert.Equal("0199a213-81c0-7800-8aa1-bbab2a035a53", chat.Session);
        Assert.Equal(24763, chat.InputTokens);
        Assert.Equal(122, chat.OutputTokens);
        Assert.False(chat.Running);

        HexAgentEntry[] tools = [.. chat.Entries.Where(entry => entry.Kind == HexAgentEntryKind.Tool)];
        Assert.Equal(["command", "mcp__hex__hex_read", "file_change"], tools.Select(tool => tool.ToolName));
        Assert.Equal(HexAgentToolState.Failed, tools[0].State);
        Assert.Equal("Failed: 1, Passed: 41", tools[0].Output);
        Assert.Equal(HexAgentToolState.Done, tools[1].State);
        Assert.Equal("update Hex.Core/Mcp/McpSchema.cs", tools[2].Output);
        Assert.Contains(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Note && entry.Text == "[x] fix it\n[ ] test again");
        Assert.Equal("One test failed, fixed it.", chat.Entries.Last(entry => entry.Kind == HexAgentEntryKind.Text).Text);
    }

    [Fact]
    public void Codex_runs_exec_json_with_hex_given_as_config_and_resumes_its_thread()
    {
        var (runtime, _, run) = Load("codex");
        var chat = new HexAgentChat(1, "codex");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);

        string[] first = Arguments(runtime, run, api);
        Assert.Equal(["exec", "--json", "--skip-git-repo-check", "--sandbox", "workspace-write",
            "-c", "mcp_servers.hex.command=\"C:/Hex/Hex.exe\"",
            "-c", "mcp_servers.hex.args=[\"--mcp-connect\",\"--folder\",\"C:/work\"]", "-"], first);

        chat.Session = "thread-1";
        Assert.Equal(["resume", "thread-1", "-"], Arguments(runtime, run, api)[^3..]);
    }
}
