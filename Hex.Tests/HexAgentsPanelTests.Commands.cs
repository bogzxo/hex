using System.Text.Json.Nodes;

using Hex.Core;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Tests;

public sealed partial class HexAgentsPanelTests
{
    [Fact]
    public void Commands_keep_metadata_deduplicate_and_never_cut_off_the_ninth_match()
    {
        var commands = HexAgentCommand.Read(HexJson.Parse("""[{"name":"/review","description":"Review code","argumentHint":"[instructions]"},{"name":"REVIEW","about":"duplicate"},{"name":"bad name"},{"name":""}]"""));
        var command = Assert.Single(commands);
        Assert.Equal(new HexAgentCommand("review", "Review code", "[instructions]"), command);
        var matches = HexAgentCommand.Matching(Enumerable.Range(0, 32).Select(i => new HexAgentCommand($"task-{i:00}")), "TASK");
        Assert.Equal(32, matches.Count);
        Assert.Equal("task-31", matches[^1].Name);
        Assert.True(HexAgentCommand.TryParse(" /review\tcheck errors\ncarefully ", out string name, out string arguments));
        Assert.Equal("review", name);
        Assert.Equal("check errors\ncarefully", arguments);
        Assert.False(HexAgentCommand.TryParse("normal text /review", out _, out _));
        Assert.False(HexAgentCommand.TryParse("/src/Parser.cs has a bug", out _, out _));
        Assert.False(HexAgentCommand.TryParse("//server/share is unavailable", out _, out _));
    }

    [Fact]
    public void Claude_discovers_skills_before_the_first_turn_and_ignores_a_previous_workspace()
    {
        var (runtime, hex, _) = Load("claude_code");
        ObjectValue announced = default;
        string workspace = folder;
        var probes = new List<(string Folder, IRuntimeValue Reply)>();
        hex.Properties["agent"] = Natives.Action("agent", args => announced = args.Object(0));
        hex.Properties["workspace"] = Natives.Function("workspace", _ => Natives.List([workspace]));
        hex.Properties["program_output"] = Natives.Action("program_output", args =>
        {
            JsonNode request = JsonNode.Parse(args.Text(4))!;
            Assert.Equal("control_request", request["type"]!.GetValue<string>());
            Assert.Equal("initialize", request["request"]!["subtype"]!.GetValue<string>());
            probes.Add((args.Text(3), args.Callable(2)));
        });

        runtime.Invoke(runtime.UserScope.Variables["on_start"], hex);
        Assert.Equal(folder, Assert.Single(probes).Folder);
        workspace = Path.Combine(folder, "another");
        runtime.Invoke(runtime.UserScope.Variables["on_workspace_changed"], hex);
        Assert.Equal(workspace, probes[1].Folder);

        runtime.Invoke(probes[1].Reply, hex, new StringValue("""
            {"type":"control_response","response":{"request_id":"hex-catalog","subtype":"success","response":{"commands":[{"name":"project-skill","description":"Explain the project","argumentHint":"[topic]"}]}}}
            """));
        runtime.Invoke(probes[0].Reply, hex, new StringValue("""
            {"type":"control_response","response":{"request_id":"hex-catalog","subtype":"success","response":{"commands":[{"name":"old-skill"}]}}}
            """));

        var run = (ObjectValue)announced.Properties["run"];
        var commands = HexAgentCommand.Read(run.Properties["commands"]);
        Assert.Equal(new HexAgentCommand("project-skill", "Explain the project", "[topic]"), Assert.Single(commands));
    }

    [Fact]
    public void Claude_refreshes_descriptions_without_a_name_change_and_keeps_aliases()
    {
        var (runtime, hex, run) = Load("claude_code");
        ObjectValue? announced = null;
        hex.Properties["agent"] = Horizon.HIDL.Library.Natives.Action("agent", args => announced = args.Object(0));
        var chat = HexAgentChatApi.Of(new HexAgentChat(1, "claude_code"), folder);
        Feed(runtime, run, chat, """{"type":"control_response","response":{"subtype":"success","request_id":"hex-init-1","response":{"commands":[{"name":"plugin:check","description":"","argumentHint":""}]}}}""");
        Feed(runtime, run, chat, """{"type":"control_response","response":{"subtype":"success","request_id":"hex-init-1","response":{"commands":[{"name":"plugin:check","description":"Check all files","argumentHint":"[path]","aliases":["check"]}]}}}""");
        var commands = HexAgentCommand.Read(((ObjectValue)announced!.Value.Properties["run"]).Properties["commands"]);
        Assert.Contains(commands, each => each.Name == "plugin:check" && each.About == "Check all files" && each.Hint == "[path]");
        Assert.Contains(commands, each => each.Name == "check" && each.About == "Check all files");
        // Metadata alone changing must also invalidate the advertised list.
        Feed(runtime, run, chat, """{"type":"control_response","response":{"subtype":"success","request_id":"hex-init-1","response":{"commands":[{"name":"plugin:check","description":"Updated help","argumentHint":"[path]","aliases":["check"]}]}}}""");
        Assert.All(HexAgentCommand.Read(((ObjectValue)announced!.Value.Properties["run"]).Properties["commands"]), each => Assert.Equal("Updated help", each.About));
    }

    [Fact]
    public void Claude_local_command_output_is_visible_and_does_not_acknowledge_queued_messages()
    {
        var (runtime, _, run) = Load("claude_code");
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("/usage");
        chat.Steer("later message");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);
        Feed(runtime, run, api,
            """{"type":"user","isReplay":true,"message":{"content":"<local-command-stdout>Current usage</local-command-stdout>"}}""",
            """{"type":"user","message":{"content":[{"type":"text","text":"<local-command-stderr>Unknown command</local-command-stderr>"}]}}""");
        Assert.True(chat.HasQueued);
        Assert.Contains(chat.Entries, each => each.Kind == HexAgentEntryKind.Note && each.Text == "Current usage");
        Assert.Contains(chat.Entries, each => each.Kind == HexAgentEntryKind.Error && each.Text == "Unknown command");
    }

    [Theory]
    [InlineData("/review", "uncommittedChanges")]
    [InlineData("/review\tcheck exception handling", "custom")]
    public void Codex_review_uses_the_review_protocol(string asked, string target)
    {
        var (runtime, _, run) = Load("codex");
        var chat = new HexAgentChat(1, "codex") { Session = "t-1" };
        var written = new List<string>();
        ObjectValue api = HexAgentChatApi.Of(chat, folder, write: line => { written.Add(line); return true; });
        Arguments(runtime, run, api);
        Said(runtime, run, api, asked);
        Feed(runtime, run, api, """{"id":2,"result":{"thread":{"id":"t-1"}}}""");
        JsonNode request = JsonNode.Parse(Assert.Single(written))!;
        Assert.Equal("review/start", request["method"]!.GetValue<string>());
        Assert.Equal(target, request["params"]!["target"]!["type"]!.GetValue<string>());
        if (target == "custom") Assert.Equal("check exception handling", request["params"]!["target"]!["instructions"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Codex_skills_wait_for_discovery_and_use_structured_input_in_either_response_order(bool skillsFirst)
    {
        var (runtime, _, run) = Load("codex");
        var chat = new HexAgentChat(1, "codex");
        var written = new List<string>();
        ObjectValue api = HexAgentChatApi.Of(chat, folder, write: line => { written.Add(line); return true; });
        Arguments(runtime, run, api);
        Assert.Contains("skills/list", Said(runtime, run, api, "/fix\nrepair the parser"));
        const string skills = """{"id":5,"result":{"data":[{"skills":[{"name":"fix","description":"Repair code","enabled":true,"path":"C:/skills/fix/SKILL.md"},{"name":"off","enabled":false,"path":"C:/off"}]}]}}""";
        const string thread = """{"id":2,"result":{"thread":{"id":"t-1"}}}""";
        Feed(runtime, run, api, skillsFirst ? skills : thread);
        Assert.Empty(written);
        Feed(runtime, run, api, skillsFirst ? thread : skills);
        JsonNode request = JsonNode.Parse(Assert.Single(written))!;
        Assert.Equal("turn/start", request["method"]!.GetValue<string>());
        Assert.Equal("$fix repair the parser", request["params"]!["input"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("skill", request["params"]!["input"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("C:/skills/fix/SKILL.md", request["params"]!["input"]![1]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void Codex_read_only_commands_do_not_start_a_model_turn_and_unknown_commands_do_not_become_prompts()
    {
        var (runtime, _, run) = Load("codex");
        var chat = new HexAgentChat(1, "codex");
        var written = new List<string>();
        ObjectValue api = HexAgentChatApi.Of(chat, folder, write: line => { written.Add(line); return true; });
        Arguments(runtime, run, api);
        chat.Ask("/skills");
        string opening = Said(runtime, run, api, "/skills");
        Assert.DoesNotContain("thread/start", opening);
        Feed(runtime, run, api, """{"id":5,"result":{"data":[]}}""");
        Assert.Empty(written);
        Assert.False(chat.Running);
        Assert.Contains(chat.Entries, each => each.Text == "No enabled skills were found.");

        Arguments(runtime, run, api);
        chat.Ask("/missing");
        Said(runtime, run, api, "/missing");
        Feed(runtime, run, api, """{"id":2,"result":{"thread":{"id":"t-1"}}}""", """{"id":5,"result":{"data":[]}}""");
        Assert.Empty(written);
        Assert.False(chat.Running);
        Assert.Contains(chat.Entries, each => each.Kind == HexAgentEntryKind.Error && each.Text.Contains("Unknown or unavailable"));
    }

    [Theory]
    [InlineData("/src/Parser.cs has a bug")]
    [InlineData("/")]
    public void Codex_sends_leading_paths_and_a_bare_slash_as_prompt_text(string prompt)
    {
        var (runtime, _, run) = Load("codex");
        var chat = new HexAgentChat(1, "codex");
        var written = new List<string>();
        ObjectValue api = HexAgentChatApi.Of(chat, folder, write: line => { written.Add(line); return true; });
        Arguments(runtime, run, api);
        Said(runtime, run, api, prompt);
        Feed(runtime, run, api, """{"id":2,"result":{"thread":{"id":"t-1"}}}""");

        JsonNode request = JsonNode.Parse(Assert.Single(written))!;
        Assert.Equal("turn/start", request["method"]!.GetValue<string>());
        Assert.Equal(prompt, request["params"]!["input"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Antigravity_builtins_use_print_mode_and_show_the_result_without_a_streamed_step()
    {
        var (runtime, _, run) = Load("antigravity");
        var chat = new HexAgentChat(1, "antigravity");
        ObjectValue api = HexAgentChatApi.Of(chat, folder);
        string[] arguments = [.. ((ListValue)runtime.Invoke(run.Properties["arguments"], new StringValue("/usage"), api)).Items.Select(Values.Text)];
        Assert.Contains("-p", arguments);
        Assert.Contains("/usage", arguments);
        Assert.DoesNotContain("--input-format", arguments);
        Assert.Equal("\"\"", Said(runtime, run, api, "/usage"));
        chat.Ask("/usage");
        Feed(runtime, run, api, """{"event":"result","result":{"status":"SUCCESS","response":"Available quota","command":{"name":"usage"}}}""");
        Assert.False(chat.Running);
        Assert.Contains(chat.Entries, each => each.Text == "Available quota");
        // A skill goes through the regular chat stream, not the standalone command runner.
        Assert.Contains("\"event\":\"user\"", Said(runtime, run, api, "/my-skill arguments"));
    }
}
