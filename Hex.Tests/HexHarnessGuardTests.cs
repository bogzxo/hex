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
    public void Every_command_of_a_line_counts_a_cd_out_of_the_workspace_letting_the_rest_be()
    {
        string elsewhere = OperatingSystem.IsWindows() ? @"D:\elsewhere" : "/elsewhere";
        Assert.Contains("hex_search", Bash("cd Hex.Core && grep -rn Foo ."));
        Assert.Contains("hex_read", Bash("git status; cat Hex/Program.cs"));
        Assert.Contains("hex_files", Bash("dotnet build || ls Hex.Dawn"));
        Assert.Contains("hex_search", Bash("git grep -n LoadSettings -- '*.cs'"));
        Assert.Contains("hex_files", Bash("git ls-files | head"));
        Assert.Contains("hex_search", Bash($"git -C {FOLDER} grep Foo"));
        Assert.Null(Bash($"git -C {elsewhere} grep Foo"));
        // A relative path going up out of the workspace is outside it too, one staying in isn't
        Assert.Null(Bash("git -C ../../outside ls-files"));
        Assert.Null(Bash("cat ../../outside/notes.txt"));
        Assert.Contains("hex_read", Bash("cat ../" + Path.GetFileName(FOLDER) + "/Hex/Program.cs"));
        Assert.Null(Bash($"cd {elsewhere} && grep -rn Foo ."));
        Assert.Contains("hex_search", Bash($"cd {elsewhere} && cd {FOLDER} && grep -rn Foo ."));
        Assert.Null(Bash("echo 'a && grep x y' | wc -l"));
        Assert.Null(Bash("git log --oneline -5 && git diff --stat"));
        Assert.Contains("hex_files", Bash($"$files = Get-ChildItem -LiteralPath '{Path.Combine(FOLDER, "Hex.Core")}' -File; $files.Count"));
    }

    [Fact]
    public void Strict_turns_back_the_agents_own_file_tools_in_the_workspace_and_only_there()
    {
        string elsewhere = OperatingSystem.IsWindows() ? @"D:\elsewhere\notes.md" : "/elsewhere/notes.md";
        string? Strict(string tool, JsonObject input) => Ask(tool, input, HexHarnessGuard.Mode.Strict);

        JsonObject said = JsonNode.Parse(Strict("Read", new JsonObject { ["file_path"] = "Hex/Program.cs" })!)!["hookSpecificOutput"]!.AsObject();
        Assert.Equal("deny", said["permissionDecision"]!.GetValue<string>());
        Assert.Contains("hex_read", said["permissionDecisionReason"]!.GetValue<string>());
        Assert.Contains("hex_edit", Strict("Edit", new JsonObject { ["file_path"] = Path.Combine(FOLDER, "Hex", "Program.cs") }));
        Assert.Contains("hex_edit", Strict("MultiEdit", new JsonObject { ["file_path"] = "a.cs" }));
        Assert.Contains("hex_write", Strict("Write", new JsonObject { ["file_path"] = "new.cs" }));
        Assert.Contains("hex_notebook_edit", Strict("NotebookEdit", new JsonObject { ["notebook_path"] = "a.ipynb" }));
        Assert.Contains("hex_search", Strict("Bash", new JsonObject { ["command"] = "grep -rn Foo ." }));

        // Outside the workspace its own tools are its own, and insist leaves them alone everywhere
        Assert.Null(Strict("Read", new JsonObject { ["file_path"] = elsewhere }));
        Assert.Null(Strict("Write", new JsonObject { ["file_path"] = elsewhere }));
        Assert.Null(Ask("Edit", new JsonObject { ["file_path"] = "Hex/Program.cs" }));

        Assert.Equal(HexHarnessGuard.Mode.Strict, HexHarnessGuard.ModeOf("strict"));
        Assert.Equal(HexHarnessGuard.Mode.Strict, HexHarnessGuard.ModeOf(null));
        Assert.Equal(HexHarnessGuard.Mode.Insist, HexHarnessGuard.ModeOf("insist"));
        Assert.Equal(HexHarnessGuard.Mode.Remind, HexHarnessGuard.ModeOf("remind"));
    }

    [Theory]
    [InlineData("let a = 1;\nlet b = 2;\n", "let b = 2;", "let b = 3;", false, "let a = 1;\nlet b = 3;\n", 1, 2, null)]
    [InlineData("x x x", "x", "y", true, "y y y", 3, 1, null)]
    [InlineData("x x x", "x", "y", false, "x x x", 3, 0, "3 times")]
    [InlineData("abc", "nope", "y", false, "abc", 0, 0, "isn't in it")]
    public void An_edit_replaces_what_it_finds_once_or_every_place_asked(string text, string old, string with, bool all, string expected, int count, int line, string? wrong)
    {
        string? said = Hex.Dawn.DawnScene.Replaced(text, old, with, all, out string changed, out int found, out int at);
        Assert.Equal(expected, changed);
        Assert.Equal(count, found);
        Assert.Equal(line, at);
        if (wrong is null)
            Assert.Null(said);
        else
            Assert.Contains(wrong, said);
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
