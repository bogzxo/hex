using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The hook that keeps an agent on Hex's harness. A search or read of the workspace by the shell or by the agent's own
/// Grep and Glob is turned back with the Hex tool for it, or let through with that said when it only reminds, and the
/// shell's other work, a grep of a pipe and anything outside the workspace are left alone.
/// </summary>
public sealed class HexHarnessGuardTests
{
    private static readonly string FOLDER = OperatingSystem.IsWindows() ? @"C:\work\hex" : "/work/hex";

    private static string? Ask(string tool, JsonObject input, HexHarnessGuard.Mode mode = HexHarnessGuard.Mode.Insist) =>
        HexHarnessGuard.Answer(((JsonNode)new JsonObject { ["tool_name"] = tool, ["tool_input"] = input }).ToJsonString(), [FOLDER], mode);

    private static string? Bash(string command) => Ask("Bash", new JsonObject { ["command"] = command });

    [Fact]
    public void A_shell_search_or_read_of_the_workspace_is_turned_back_with_the_hex_tool_for_it()
    {
        JsonObject said = JsonNode.Parse(Bash("grep -rn LoadSettings Hex.Core")!)!["hookSpecificOutput"]!.AsObject();
        Assert.Equal("PreToolUse", said["hookEventName"]!.GetValue<string>());
        Assert.Equal("deny", said["permissionDecision"]!.GetValue<string>());
        Assert.Contains("hex_search", said["permissionDecisionReason"]!.GetValue<string>());

        Assert.Contains("hex_read", Bash("cat Hex.Core/Agents/HexAgentChat.cs"));
        Assert.Contains("hex_files", Bash("find . -name '*.cs'"));
        Assert.Contains("hex_read", Bash("sed -n 10,40p Hex/Program.cs"));
        Assert.Contains("hex_search", Bash("Select-String -Path *.cs -Pattern Foo"));
        Assert.Contains("hex_search", Ask("Grep", new JsonObject { ["pattern"] = "Foo" }));
        Assert.Contains("hex_files", Ask("Glob", new JsonObject { ["pattern"] = "**/*.cs" }));
    }

    [Fact]
    public void The_shells_other_work_a_filtered_pipe_and_places_outside_the_workspace_are_left_alone()
    {
        Assert.Null(Bash("git log --oneline | grep fix"));
        Assert.Null(Bash("dotnet test Hex.Tests"));
        Assert.Null(Bash("sed -i 's/a/b/' file.txt"));
        Assert.Null(Bash("ls ~/.codex/sessions"));
        Assert.Null(Bash(OperatingSystem.IsWindows() ? @"cat C:\Users\me\notes.txt" : "cat /etc/hosts"));
        Assert.Null(Ask("Grep", new JsonObject { ["pattern"] = "x", ["path"] = OperatingSystem.IsWindows() ? @"D:\elsewhere" : "/elsewhere" }));
        Assert.Null(Ask("Read", new JsonObject { ["file_path"] = "Hex/Program.cs" }));
        Assert.Null(HexHarnessGuard.Answer("not json", [FOLDER], HexHarnessGuard.Mode.Insist));
    }

    [Fact]
    public void Reminding_lets_it_run_with_the_hex_tool_said_beside_it()
    {
        JsonObject said = JsonNode.Parse(Ask("Bash", new JsonObject { ["command"] = "rg Foo" }, HexHarnessGuard.Mode.Remind)!)!["hookSpecificOutput"]!.AsObject();
        Assert.Null(said["permissionDecision"]);
        Assert.Contains("hex_search", said["additionalContext"]!.GetValue<string>());
    }

    [Fact]
    public void A_git_bash_path_into_the_workspace_is_inside_it()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(HexHarnessGuard.Inside("/c/work/hex/Hex.Core", [FOLDER]));
        Assert.False(HexHarnessGuard.Inside("/c/work/other", [FOLDER]));
        Assert.True(HexHarnessGuard.Inside("Hex.Core/Mcp", [FOLDER]));
    }
}
