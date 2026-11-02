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
            ("exists", Natives.Function("exists", args => Values.Bool(existing is not null && args.Text(0).EndsWith(".md")))),
            ("read", Natives.Function("read", _ => new StringValue(existing ?? string.Empty))),
            ("write", Natives.Action("write", args => written[Path.GetFileName(args.Text(0))] = args.Text(1))),
            ("ask", Natives.Action("ask", args => runtime.Invoke(args.Callable(1), hex, new StringValue("Add it")))));

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
}
