using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// What sits around the agent harness. Profiles picked by the client's name and what they change, the server telling
/// a client its own instructions, saying who's calling and having resources to subscribe to, the terminal's marker
/// read out of a screen, JSON set for an extension, and the Claude Code and Codex extensions loaded and saying who they are.
/// </summary>
[Collection(Timed.NAME)]
public sealed class HexAgentSystemsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-agents-" + Guid.NewGuid().ToString("N")[..8]);

    public HexAgentSystemsTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        HexAgentProfiles.Forget("test");
        HexAgentProfiles.Forget("claude_code");
        HexAgentProfiles.Forget("codex");
        HexAgentProfiles.Forget("gemini");
        HexAgentProfiles.Forget("antigravity");
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    [Fact]
    public void A_profile_is_picked_by_the_clients_name_and_sets_the_budget_and_instructions()
    {
        HexAgentProfiles.Set(new HexAgentProfile("tester", "Tester", ["test-client"], 1234, "Use the harness.", "test"));
        Assert.Equal("Tester", HexAgentProfiles.For("test-client 2.0")?.Name);
        Assert.Null(HexAgentProfiles.For("someone-else"));
        Assert.Equal(1234, HexAgentProfiles.BudgetFor("test-client 2.0", null));
        Assert.Equal(5000, HexAgentProfiles.BudgetFor("test-client 2.0", 5000));
        Assert.Equal(HexAgentProfiles.USUAL_BUDGET, HexAgentProfiles.BudgetFor(null, null));
        Assert.EndsWith("Tester: Use the harness.", HexAgentProfiles.InstructionsFor("test-client", "Hex's."));

        // Said again it keeps its place among the others
        HexAgentProfiles.Set(new HexAgentProfile("second", "Second", ["second-client"], 0, string.Empty, "test"));
        HexAgentProfiles.Set(new HexAgentProfile("tester", "Tester", ["test-client"], 999, "Again.", "test"));
        Assert.Equal(["tester", "second"], HexAgentProfiles.All.Where(each => each.Extension == "test").Select(each => each.Id));
        Assert.Equal(999, HexAgentProfiles.BudgetFor("test-client", null));

        HexAgentProfiles.Forget("test");
        Assert.Null(HexAgentProfiles.For("test-client"));
    }

    // A tool that says who called it, for the server's calling client
    private sealed class WhoAsks : IMcpTools
    {
        public IReadOnlyList<McpTool> Tools { get; } = [new McpTool("who", "Who", "Says who's calling.", McpSchema.Empty(), ReadOnly: true)];

        public Task<McpToolResult> Call(string name, JsonObject arguments, CancellationToken cancel) =>
            Task.Run(() => McpToolResult.Text(McpServer.CallingClient.Value ?? "nobody"), cancel);
    }

    private static async Task<JsonObject> Ask(McpServer server, int id, string method, JsonObject? parameters = null) =>
        (JsonObject)(await server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] }))!;

    private static JsonObject Initialize(string client) =>
        new() { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = client, ["version"] = "1" } };

    [Fact]
    public async Task The_server_tells_a_client_its_own_instructions_and_who_is_calling()
    {
        HexAgentProfiles.Set(new HexAgentProfile("tester", "Tester", ["test-client"], 0, "Find before reading.", "test"));
        var server = new McpServer(new WhoAsks(), "hex", "1", "Hex's.") { InstructionsFor = client => HexAgentProfiles.InstructionsFor(client, "Hex's.") };

        JsonObject started = await Ask(server, 1, "initialize", Initialize("test-client"));
        Assert.Equal("Hex's.\n\nTester: Find before reading.", started["result"]!["instructions"]!.GetValue<string>());

        JsonObject called = await Ask(server, 2, "tools/call", new JsonObject { ["name"] = "who", ["arguments"] = new JsonObject() });
        Assert.Equal("test-client 1", called["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_client_subscribed_to_a_resource_is_told_when_it_changes_and_not_after_it_goes()
    {
        Action? changed = null;
        int watching = 0;
        var resources = new McpResources([("hex://test", "Test", "A test.")], uri => uri == "hex://test" ? "what changed" : null, (uri, told) =>
        {
            changed = told;
            watching++;
            return new Stop(() => watching--);
        });

        var server = new McpServer(new WhoAsks(), "hex", "1") { Resources = resources };
        var notes = new List<JsonNode>();
        server.Notify = notes.Add;

        JsonObject started = await Ask(server, 1, "initialize", Initialize("a"));
        Assert.True(started["result"]!["capabilities"]!["resources"]!["subscribe"]!.GetValue<bool>());
        Assert.Equal("hex://test", (await Ask(server, 2, "resources/list"))["result"]!["resources"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal("what changed", (await Ask(server, 3, "resources/read", new JsonObject { ["uri"] = "hex://test" }))["result"]!["contents"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull((await Ask(server, 4, "resources/read", new JsonObject { ["uri"] = "hex://nope" }))["error"]);

        await Ask(server, 5, "resources/subscribe", new JsonObject { ["uri"] = "hex://test" });
        changed!();
        Assert.Equal("notifications/resources/updated", notes.Single()["method"]!.GetValue<string>());
        Assert.Equal(1, watching);

        server.Forget();
        Assert.Equal(0, watching);
    }

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    [Fact]
    public void The_marker_is_written_the_way_each_shell_says_its_exit_code()
    {
        Assert.Equal("make; echo \"__HEX_x_$?_\"", DawnScene.McpMarked("make", "sh", "__HEX_x"));
        Assert.Equal("make & call echo __HEX_x_%^errorlevel%_", DawnScene.McpMarked("make", "cmd", "__HEX_x"));
        Assert.Contains("$LASTEXITCODE", DawnScene.McpMarked("make", "pwsh", "__HEX_x"));

        // Git never pages for an agent, every statement of the line that's git, and a line that said so already as it was
        Assert.Equal("git --no-pager diff --stat; git --no-pager log -1", DawnScene.McpNoPager("git diff --stat; git log -1"));
        Assert.Equal("dotnet build && git --no-pager status", DawnScene.McpNoPager("dotnet build && git status"));
        Assert.Equal("git --no-pager log", DawnScene.McpNoPager("git --no-pager log"));
        Assert.Equal("echo digit stuff", DawnScene.McpNoPager("echo digit stuff"));
        Assert.Equal("sh", DawnScene.McpShellOf(new Horizon.UI.Components.TerminalProfile("Git Bash", @"C:\Program Files\Git\bin\bash.exe")));
        Assert.Equal("pwsh", DawnScene.McpShellOf(new Horizon.UI.Components.TerminalProfile("PowerShell", "pwsh.exe")));
        Assert.Equal("cmd", DawnScene.McpShellOf(new Horizon.UI.Components.TerminalProfile("Command Prompt", "cmd.exe")));
    }

    [Fact]
    public void A_commands_output_is_what_came_between_what_was_typed_and_the_marker()
    {
        string screen = "old stuff\n$ make; echo \"__HEX_ab_$?_\"\r\nbuilding\nbuilt\n__HEX_ab_2_\n$ ";
        var (output, exit) = DawnScene.McpRunOutput(screen, "__HEX_ab");
        Assert.Equal("building\nbuilt", output);
        Assert.Equal(2, exit);

        var (going, none) = DawnScene.McpRunOutput("$ make; echo \"__HEX_ab_$?_\"\nbuilding", "__HEX_ab");
        Assert.Equal("building", going);
        Assert.Null(none);
    }

    [Fact]
    public void Long_output_is_cut_in_the_middle()
    {
        string output = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"line {i}"));
        string cut = DawnScene.McpMiddleCut(output, 1000);
        Assert.StartsWith("line 0\n", cut);
        Assert.EndsWith("line 1999", cut);
        Assert.Contains("cut from the middle", cut);
        Assert.True(cut.Length < 1100);
    }

    [Fact]
    public void Json_set_puts_a_key_in_and_keeps_the_rest_of_the_file()
    {
        string file = Path.Combine(folder, ".mcp.json");
        File.WriteAllText(file, "{ \"mcpServers\": { \"other\": { \"command\": \"x\" } }, // a comment\n \"keep\": 1 }");
        string? why = HexExtensions.JsonSet(file, ["mcpServers", "hex"], Natives.Object(("command", new StringValue("Hex.exe")), ("args", Natives.List(["--mcp"]))));
        Assert.Null(why);

        JsonObject written = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal("x", written["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal("--mcp", written["mcpServers"]!["hex"]!["args"]![0]!.GetValue<string>());
        Assert.Equal(1, written["keep"]!.GetValue<int>());

        File.WriteAllText(file, "[1, 2]");
        Assert.Contains("isn't a JSON object", HexExtensions.JsonSet(file, ["a"], new StringValue("b")));
    }

    /// <summary>A catalog extension run against a hex with just what an agent system uses, and the hex it ran against.</summary>
    private (HIDLRuntime Runtime, ObjectValue Hex, Dictionary<string, IRuntimeValue> Commands, Dictionary<string, string> Written) Load(string id, string? existing = null)
    {
        var commands = new Dictionary<string, IRuntimeValue>();
        var written = new Dictionary<string, string>();
        HIDLRuntime runtime = null!;
        ObjectValue hex = default;
        hex = Natives.Object(
            ("agent", Natives.Action("agent", args => HexAgentProfiles.Set(new HexAgentProfile(
                HexExtensions.Text(args.Object(0), "id")!, HexExtensions.Text(args.Object(0), "name")!,
                [.. ((ListValue)args.Object(0).Properties["clients"]).Items.Select(Values.Text)],
                (int)((NumberValue)args.Object(0).Properties["budget"]).Value, HexExtensions.Text(args.Object(0), "instructions")!, id)))),
            ("workspace", Natives.Function("workspace", _ => Natives.List([folder]))),
            ("executable", new StringValue(@"C:\Hex\Hex.exe")),
            ("home", new StringValue(folder)),
            ("json_set", Natives.Function("json_set", args =>
                HexExtensions.JsonSet(args.Text(0), [.. ((ListValue)args[1]).Items.Select(Values.Text)], args[2]) is { } why ? new StringValue(why) : Values.Null)),
            ("setting", Natives.Function("setting", args => args.Text(0) switch { "budget" => new NumberValue(7000), _ => new StringValue("hex") })),
            ("command", Natives.Action("command", args => commands[args.Text(0)] = args.Callable(1))),
            ("say", Natives.Action("say", _ => { })),
            ("notify", Natives.Action("notify", _ => { })),
            ("exists", Natives.Function("exists", args => Values.Bool(existing is not null && args.Text(0).EndsWith(".md") || args.Text(0).EndsWith(".json") && File.Exists(args.Text(0))))),
            ("read", Natives.Function("read", args => new StringValue(args.Text(0).EndsWith(".json") && File.Exists(args.Text(0)) ? File.ReadAllText(args.Text(0)) : existing ?? string.Empty))),
            ("write", Natives.Action("write", args => written[Path.GetFileName(args.Text(0))] = args.Text(1))),
            ("ask", Natives.Action("ask", args => runtime.Invoke(args.Callable(1), hex, new StringValue("Add it")))),
            ("find_file", Natives.Function("find_file", args => HexExtensions.FindFile(args.Text(0), args.Text(1)) is { } found ? new StringValue(found) : Values.Null)),
            ("read_lines", Natives.Function("read_lines", args =>
            {
                var (lines, end) = HexExtensions.ReadLinesFrom(args.Text(0), (long)((NumberValue)args[1]).Value);
                return Natives.Object(("lines", new ListValue([.. lines.Select(line => (IRuntimeValue)new StringValue(line))])), ("end", new NumberValue(end)));
            })),
            ("json_parse", Natives.Function("json_parse", args => HexJson.Parse(args.Text(0)) ?? Values.Null)),
            ("temp", new StringValue(folder)),
            ("mcp_server", Natives.Function("mcp_server", _ => Natives.Object(("command", new StringValue("Hex.exe")), ("args", Natives.List(["--mcp-connect"]))))));

        string extension = Path.Combine(Catalog.Folder, id);
        runtime = new HIDLRuntime { BaseDirectory = extension, Output = null };
        runtime.UserScope.DeclareSystem("hex", hex);
        runtime.RunFile(Path.Combine(extension, "extension.hor"));
        runtime.Invoke(runtime.UserScope.Variables["on_start"], hex);
        return (runtime, hex, commands, written);
    }

    [Fact]
    public void Claude_code_says_who_it_is_and_writes_the_server_into_the_folders_mcp_json()
    {
        var (runtime, hex, commands, _) = Load("claude_code");
        HexAgentProfile? profile = HexAgentProfiles.For("claude-code 2.1.0");
        Assert.Equal("Claude Code", profile?.Name);
        Assert.Equal(7000, profile?.Budget);
        Assert.Contains("hex_symbols", profile?.Instructions);

        runtime.Invoke(commands["Connect Claude Code to this folder"], hex);
        JsonObject config = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ".mcp.json")))!.AsObject();
        Assert.Equal(@"C:\Hex\Hex.exe", config["mcpServers"]!["hex"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Teaching_puts_the_block_in_place_of_the_one_there_rather_than_twice()
    {
        var (runtime, hex, commands, written) = Load("claude_code", "# Mine\n\n<!-- hex tools -->\nold\n<!-- /hex tools -->\n\nafter\n");
        runtime.Invoke(commands["Teach Claude Code the Hex tools"], hex);
        string text = written["CLAUDE.md"];
        Assert.StartsWith("# Mine\n\n<!-- hex tools -->\n## Finding code", text);
        Assert.EndsWith("<!-- /hex tools -->\n\nafter\n", text);
        Assert.DoesNotContain("\nold\n", text);
    }

    [Fact]
    public void Codex_says_who_it_is_and_adds_the_server_to_its_config_once_asked()
    {
        var (runtime, hex, commands, written) = Load("codex");
        Assert.Equal("Codex", HexAgentProfiles.For("codex-mcp-client")?.Name);

        runtime.Invoke(commands["Connect Codex to Hex"], hex);
        Assert.Equal("[mcp_servers.hex]\ncommand = 'C:\\Hex\\Hex.exe'\nargs = [\"--mcp-connect\"]\n", written["config.toml"]);
    }

    /// <summary>The lines given played through an extension's on_event into a chat, as its agent would print them.</summary>
    private HexAgentChat Play(string id, params string[] lines)
    {
        var (runtime, hex, _, _) = Load(id);
        IRuntimeValue run = Agents(runtime, hex);
        var chat = new HexAgentChat(1, id);
        chat.Ask("go");
        ObjectValue said = HexAgentChatApi.Of(chat, folder);
        foreach (string line in lines)
            runtime.Invoke(((ObjectValue)run).Properties["on_event"], said, HexJson.Parse(line)!);
        return chat;
    }

    // The run object the extension handed hex.agent, said again to a hex that keeps it
    private static IRuntimeValue Agents(HIDLRuntime runtime, ObjectValue hex)
    {
        IRuntimeValue? run = null;
        var keeping = Natives.Object([.. hex.Properties.Select(pair => (pair.Key, pair.Value)).Where(pair => pair.Key != "agent"),
            ("agent", Natives.Action("agent", args => run = args.Object(0).Properties["run"]))]);
        runtime.Invoke(runtime.UserScope.Variables["say_who"], keeping);
        return run!;
    }

    [Fact]
    public void Claude_codes_compactions_limits_and_retries_reach_the_chat()
    {
        HexAgentChat chat = Play("claude_code",
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":2000,"error_status":529,"error":"overloaded"}""",
            """{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":4000,"error_status":529,"error":"overloaded"}""",
            """{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":47438,"post_tokens":14064}}""",
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1794933000,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.75,"resetsAt":1794933000,"status":"allowed"},"seven_day":{"utilization":0.32,"resetsAt":1795341600,"status":"allowed"}}}}""",
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1795341600,"rateLimitType":"seven_day","unifiedWindows":{"five_hour":{"utilization":0.8,"resetsAt":1794933000},"seven_day":{"utilization":1.0,"resetsAt":1795341600}}}}""");

        // Two tries are one row saying the latest, the compaction a row of its own
        Assert.Equal(2, chat.Retries);
        Assert.Single(chat.Entries, entry => entry.Text.StartsWith("Retrying, attempt 2 of 10, overloaded (529)", StringComparison.Ordinal));
        Assert.Equal(1, chat.Compactions);
        Assert.Equal(47438, chat.CompactedFrom);
        Assert.Equal(14064, chat.CompactedTo);
        Assert.Contains(chat.Entries, entry => entry.Text == "Context compacted on request, 47k to 14k tokens");

        // The binding window when it's refused, a row that fails the turn, the all clear before it no row at all
        Assert.Equal("seven_day", chat.Limit?.Kind);
        Assert.Equal(HexAgentLimitState.Hit, chat.Limit?.State);
        Assert.Equal(1.0, chat.Limit?.Used);
        // HIDL's numbers are single precision, so a time in seconds since 1970 comes through to within a minute or two
        Assert.InRange((chat.Limit!.Resets!.Value - DateTimeOffset.FromUnixTimeSeconds(1795341600)).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(2));
        Assert.True(chat.Failed);
        Assert.Single(chat.Entries, entry => entry.Text.StartsWith("Limit reached, weekly limit · 100% used", StringComparison.Ordinal));

        // Every window it said is kept, the session's beside the week's, each as the latest said it
        Assert.Equal(["five_hour", "seven_day"], chat.Limits.Keys.Order().ToArray());
        Assert.Equal(0.8, chat.Limits["five_hour"].Used, 3);
    }

    [Fact]
    public void Claude_code_knows_a_models_window_from_the_start_of_a_turn_once_it_has_seen_it()
    {
        HexAgentChat first = Play("claude_code",
            """{"type":"system","subtype":"init","session_id":"s","model":"claude-opus-5-5"}""",
            """{"type":"result","session_id":"s","usage":{"input_tokens":2},"modelUsage":{"claude-opus-5-5":{"contextWindow":1000000}}}""");
        Assert.Equal(1000000, first.ContextLimit);

        // A coordinator's first turn can last as long as its agents do, its bar is there from its first line
        HexAgentChat second = Play("claude_code", """{"type":"system","subtype":"init","session_id":"t","model":"claude-opus-5-5"}""");
        Assert.Equal(1000000, second.ContextLimit);
        Assert.Equal(1000000, Play("claude_code", """{"type":"system","subtype":"init","session_id":"u","model":"claude-sonnet-5-5[1m]"}""").ContextLimit);
    }

    [Fact]
    public void Claude_codes_todo_list_is_a_checklist_and_a_slash_commands_echo_takes_nothing_queued()
    {
        HexAgentChat chat = Play("claude_code",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TodoWrite","input":{"todos":[{"content":"Read it","status":"completed"},{"content":"Fix it","status":"in_progress"}]}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");
        Assert.Contains(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Note && entry.Text == "[x] Read it\n[~] Fix it");
        Assert.DoesNotContain(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Tool);

        chat.Steer("and this");
        var (runtime, hex, _, _) = Load("claude_code");
        runtime.Invoke(((ObjectValue)Agents(runtime, hex)).Properties["on_event"], HexAgentChatApi.Of(chat, folder),
            HexJson.Parse("""{"type":"user","isReplay":true,"message":{"content":"<local-command-stdout>Compacted </local-command-stdout>"}}""")!);
        Assert.True(chat.HasQueued);
    }

    [Fact]
    public void Codex_says_its_limits_compactions_and_retries_as_its_app_server_tells_them()
    {
        var (runtime, hex, _, _) = Load("codex");
        ObjectValue run = (ObjectValue)Agents(runtime, hex);
        var chat = new HexAgentChat(1, "codex");
        chat.Ask("go");
        ObjectValue said = HexAgentChatApi.Of(chat, folder, write: _ => true);
        void Hear(string line) => runtime.Invoke(run.Properties["on_event"], said, HexJson.Parse(line)!);

        Hear("""{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":449460,"outputTokens":10},"last":{"inputTokens":449460},"modelContextWindow":475000}}}""");
        Hear("""{"method":"item/completed","params":{"item":{"type":"contextCompaction","id":"x"}}}""");
        Hear("""{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":510460,"outputTokens":12},"last":{"inputTokens":61000},"modelContextWindow":475000}}}""");
        Hear("""{"method":"account/rateLimits/updated","params":{"rateLimits":{"primary":{"usedPercent":93.0,"windowDurationMins":10080,"resetsAt":1795286849},"secondary":null,"rateLimitReachedType":null}}}""");
        Hear("""{"method":"error","params":{"error":{"message":"stream disconnected"},"willRetry":true}}""");
        Hear("""{"method":"turn/completed","params":{"turn":{"id":"t","status":"completed"}}}""");

        Assert.Equal(1, chat.Compactions);
        Assert.Equal(61000, chat.ContextUsed);
        Assert.Equal(475000, chat.ContextLimit);
        Assert.Equal("seven_day", chat.Limit?.Kind);
        Assert.Equal(HexAgentLimitState.Warning, chat.Limit?.State);
        Assert.Equal(1, chat.Retries);
        Assert.Contains(chat.Entries, entry => entry.Text.StartsWith("Close to the limit, weekly limit · 93% used", StringComparison.Ordinal));
        Assert.False(chat.Running);
    }

    [Fact]
    public void Claude_code_runs_with_hex_as_the_hook_that_keeps_it_on_the_harness()
    {
        var (runtime, hex, _, _) = Load("claude_code");
        hex = Natives.Object([.. hex.Properties.Select(pair => (pair.Key, pair.Value)).Where(pair => pair.Key is not "harness_rules" and not "setting"),
            ("harness_rules", new StringValue(HexAgentProfiles.HARNESS_RULES)),
            ("setting", Natives.Function("setting", args => args.Text(0) switch { "budget" => new NumberValue(7000), "harness_guard" => new StringValue("insist"), "max_budget" => new NumberValue(0), _ => new StringValue("") }))]);
        ObjectValue run = (ObjectValue)Agents(runtime, hex);
        var chat = new HexAgentChat(9, "claude_code");
        string[] arguments = [.. ((ListValue)runtime.Invoke(run.Properties["arguments"], new StringValue("go"), HexAgentChatApi.Of(chat, folder))).Items.Select(Values.Text)];
        string settings = arguments[Array.IndexOf(arguments, "--settings") + 1];
        JsonObject hook = JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["PreToolUse"]![0]!.AsObject();
        Assert.Contains("Grep", hook["matcher"]!.GetValue<string>());
        string command = hook["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Contains("--harness-hook --guard insist --folder", command);
        Assert.StartsWith("\"C:/Hex/Hex.exe\"", command);
    }

    [Fact]
    public void Gemini_says_who_it_is_and_runs_with_hex_in_its_settings_and_the_session_resumed()
    {
        var (runtime, hex, commands, written) = Load("gemini");
        Assert.Equal("Gemini", HexAgentProfiles.For("gemini-cli-mcp-client 0.20")?.Name);

        ObjectValue run = (ObjectValue)Agents(runtime, hex);
        var chat = new HexAgentChat(3, "gemini") { Session = "s1" };
        IRuntimeValue made = runtime.Invoke(run.Properties["arguments"], new StringValue("go"), HexAgentChatApi.Of(chat, folder));
        string[] arguments = [.. ((ListValue)made).Items.Select(Values.Text)];
        Assert.Equal(["-p", "", "--output-format", "stream-json"], arguments[..4]);
        Assert.Equal("s1", arguments[Array.IndexOf(arguments, "--resume") + 1]);
        JsonObject settings = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ".gemini", "settings.json")))!.AsObject();
        Assert.Equal("Hex.exe", settings["mcpServers"]!["hex"]!["command"]!.GetValue<string>());
        Assert.True(settings["mcpServers"]!["hex"]!["trust"]!.GetValue<bool>());

        runtime.Invoke(commands["Teach Gemini the Hex tools"], hex);
        Assert.Contains("## Finding code", written["GEMINI.md"]);
    }

    [Fact]
    public void Geminis_stream_is_rows_its_chunks_one_answer_and_a_quota_its_limit()
    {
        HexAgentChat chat = Play("gemini",
            """{"type":"init","timestamp":"2026-11-17T10:00:00Z","session_id":"g1","model":"gemini-3-pro-preview"}""",
            """{"type":"message","role":"user","content":"go"}""",
            """{"type":"message","role":"assistant","content":"Looking ","delta":true}""",
            """{"type":"message","role":"assistant","content":"now.","delta":true}""",
            """{"type":"tool_use","tool_name":"mcp_hex_hex_search","tool_id":"t1","parameters":{"query":"Load"}}""",
            """{"type":"tool_result","tool_id":"t1","status":"success","output":"a.cs:1"}""",
            """{"type":"message","role":"assistant","content":"Found it.","delta":true}""",
            """{"type":"error","severity":"warning","message":"Quota exceeded for quota metric, 429 RESOURCE_EXHAUSTED"}""",
            """{"type":"result","status":"success","stats":{"total_tokens":120,"input_tokens":100,"output_tokens":20,"duration_ms":900,"tool_calls":1}}""");

        Assert.Equal("g1", chat.Session);
        Assert.Equal("gemini-3-pro-preview", chat.Model);
        string[] said = [.. chat.Entries.Where(entry => entry.Kind == HexAgentEntryKind.Text).Select(entry => entry.Text)];
        Assert.Equal(["Looking now.", "Found it."], said);
        Assert.Contains(chat.Entries, entry => entry is { Kind: HexAgentEntryKind.Tool, ToolId: "t1", State: HexAgentToolState.Done });
        Assert.Equal(HexAgentLimitState.Hit, chat.Limit?.State);
        Assert.Equal(100, chat.InputTokens);
        Assert.Equal(20, chat.OutputTokens);
        Assert.True(chat.ContextLimit > 1_000_000);
        Assert.False(chat.Running);
    }

    [Fact]
    public void Antigravity_runs_agy_on_stream_json_with_hex_in_its_mcp_config_and_the_conversation_gone_on_with()
    {
        var (runtime, hex, _, _) = Load("antigravity");
        ObjectValue run = (ObjectValue)Agents(runtime, hex);
        var chat = new HexAgentChat(4, "antigravity") { Session = "c1" };
        string[] arguments = [.. ((ListValue)runtime.Invoke(run.Properties["arguments"], new StringValue("go"), HexAgentChatApi.Of(chat, folder))).Items.Select(Values.Text)];
        Assert.Equal(["--output-format", "stream-json", "--input-format", "stream-json"], arguments[..4]);
        // The hex server's tools allowed for the run alone, headless agy refuses every MCP call otherwise
        JsonNode allowed = JsonNode.Parse(arguments[Array.IndexOf(arguments, "--settings") + 1])!;
        Assert.Equal("mcp(hex/*)", allowed["permissions"]!["allow"]![0]!.GetValue<string>());
        // The stub's settings are all "hex", which is a mode as far as the arguments go
        Assert.Contains("--mode", arguments);
        Assert.Equal("c1", arguments[Array.IndexOf(arguments, "--conversation") + 1]);
        Assert.DoesNotContain("-p", arguments);
        JsonObject config = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ".agents", "mcp_config.json")))!.AsObject();
        Assert.Equal("Hex.exe", config["mcpServers"]!["hex"]!["command"]!.GetValue<string>());

        // What's asked is a user line of its stream-json
        JsonNode said = HexJson.FromHidl(runtime.Invoke(run.Properties["message"], new StringValue("hello"), HexAgentChatApi.Of(chat, folder)))!;
        Assert.Equal("user", said["event"]!.GetValue<string>());
        Assert.Equal("hello", said["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Antigravitys_stream_as_agy_1_5_writes_it_is_rows_its_fragments_one_answer_and_a_tool_by_its_step()
    {
        // Lines of a real agy 1.5.11 run, the hex server's call and a failed turn added
        HexAgentChat chat = Play("antigravity",
            """{"event":"init","conversation_id":"2b7a17d3","init":{"model":"gemini-3.8-flash-low","cwd":"C:\\probe","tools":["view_file"],"permission_mode":"request-review"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":0,"state":"DONE","step_type":"user_input"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":4.3,"usage":{"input_tokens":9800,"output_tokens":35,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":9835}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"read_file","tool_info":{"name":"read_file","parameters":{"Path":"C:\\probe\\hello.txt"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":2,"state":"DONE","step_type":"tool","tool_name":"read_file","duration_seconds":0.12,"tool_info":{"name":"read_file","parameters":{"Path":"C:\\probe\\hello.txt"},"output":"1 lines, 4 bytes"}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":3,"state":"ACTIVE","step_type":"tool","tool_name":"call_mcp_tool","tool_info":{"name":"call_mcp_tool","parameters":{"Arguments":{"query":"Kept"},"ServerName":"hex","ToolName":"hex_search"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":3,"state":"ERROR","step_type":"tool","tool_name":"call_mcp_tool","tool_info":{"name":"call_mcp_tool","parameters":{"Arguments":{"query":"Kept"},"ServerName":"hex","ToolName":"hex_search"},"error":{"type":"TOOL_ERROR","message":"permission check failed for mcp \"hex/hex_search\""}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":4,"state":"ACTIVE","step_type":"agent_response","text_delta":"The file contains t"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"2b7a17d3","step_index":4,"state":"DONE","step_type":"agent_response","text_delta":"he word \"hi\".\n","duration_seconds":3.0,"usage":{"input_tokens":2164,"output_tokens":36,"thinking_tokens":0,"cache_read_tokens":7757,"total_tokens":2200}}}""",
            """{"event":"result","result":{"conversation_id":"2b7a17d3","status":"ERROR","error":{"message":"429 RESOURCE_EXHAUSTED"},"duration_seconds":7.4,"num_turns":1,"denied_actions":[{"action":"command","display_name":"Bash"}]}}""");

        Assert.Equal("2b7a17d3", chat.Session);
        Assert.Equal("gemini-3.8-flash-low", chat.Model);
        Assert.Equal(["The file contains the word \"hi\".\n"], chat.Entries.Where(entry => entry.Kind == HexAgentEntryKind.Text).Select(entry => entry.Text).ToArray());
        Assert.Contains(chat.Entries, entry => entry is { Kind: HexAgentEntryKind.Tool, ToolId: "step-2", ToolName: "read_file", State: HexAgentToolState.Done, Output: "1 lines, 4 bytes" });
        Assert.Contains(chat.Entries, entry => entry is { Kind: HexAgentEntryKind.Tool, ToolId: "step-3", ToolName: "mcp__hex__hex_search", State: HexAgentToolState.Failed }
            && entry.Input!.Contains("Kept", StringComparison.Ordinal) && !entry.Input.Contains("ServerName", StringComparison.Ordinal));
        Assert.Contains(chat.Entries, entry => entry.Kind == HexAgentEntryKind.Note && entry.Text.Contains("Bash", StringComparison.Ordinal));
        Assert.Equal(HexAgentLimitState.Hit, chat.Limit?.State);
        Assert.True(chat.ContextLimit > 1_000_000);
        Assert.False(chat.Running);
    }

    [Fact]
    public void Read_lines_goes_on_from_where_it_got_to_and_leaves_a_line_half_written()
    {
        string file = Path.Combine(folder, "log.jsonl");
        File.WriteAllText(file, "one\ntwo\nthr");
        var (lines, end) = HexExtensions.ReadLinesFrom(file, 0);
        Assert.Equal(["one", "two"], lines);
        File.AppendAllText(file, "ee\n");
        (lines, end) = HexExtensions.ReadLinesFrom(file, end);
        Assert.Equal(["three"], lines);
        Assert.Empty(HexExtensions.ReadLinesFrom(file, end).Lines);
        Assert.Equal(new FileInfo(file).Length, HexExtensions.ReadLinesFrom(file, -1).End);
    }

    [Fact]
    public void A_version_is_the_number_out_of_what_a_program_says()
    {
        Assert.Equal("2.1.326", DawnScene.VersionOf("2.1.326 (Claude Code)"));
        Assert.Equal("0.169.0", DawnScene.VersionOf("codex-cli 0.169.0"));
        Assert.Equal("1.2.3", DawnScene.VersionOf("v1.2.3"));
    }

    [Fact]
    public void A_number_hex_put_in_the_arguments_itself_reads_back_like_one_off_the_wire()
    {
        // The chat an agent's call came from goes in as an int, which wouldn't come out as a double, and every agent
        // looked like it was started from outside
        var arguments = new JsonObject { [HexMcpCaller.CALLER] = 7, ["timeout"] = JsonNode.Parse("2.5") };
        Assert.Equal(7, arguments.Integer(HexMcpCaller.CALLER));
        Assert.Equal(2.5, arguments.Number("timeout"));
        Assert.Null(arguments.Integer("missing"));
    }
}
