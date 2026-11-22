using Hex.Core;
using Hex.Dawn;
using Hex.Shell;

using Horizon.HIDL.Runtime;

namespace Hex.Tests;

/// <summary>
/// No AI's parts that stand without an editor. The agent systems of the catalog known for what they are without being
/// run, and only those, the setting read and written, its row where the settings window shows it, and the MCP tools it
/// leaves out being the ones about agents and none of the rest. The run without the agents is the self test's (no-ai),
/// HexAi is the process's and a test that switched it would take the agents out of every test after it.
/// </summary>
public sealed class NoAiTests
{
    [Fact]
    public void The_agent_systems_of_the_catalog_are_known_without_running_them()
    {
        string[] agents = [.. Directory.GetDirectories(Catalog.Folder).Where(HexExtensionCatalog.IsAgentSystem).Select(Path.GetFileName).Order()!];

        Assert.Equal(["antigravity", "claude_code", "codex"], agents);
        Assert.False(HexExtensionCatalog.IsAgentSystem(Path.Combine(Catalog.Folder, "emmet")));
        Assert.False(HexExtensionCatalog.IsAgentSystem(Path.Combine(Catalog.Folder, "no such extension")));
    }

    [Fact]
    public void No_ai_is_off_as_it_comes_and_kept_in_the_file()
    {
        Assert.False(new HexSettings(null).NoAi);
        Assert.Equal(Values.Bool(false), EditorSettingsCatalog.Usual["no_ai"]);
        Assert.Contains(EditorSettingsCatalog.Settings, setting => setting.Key == "no_ai" && setting.Group == EditorSettingsCatalog.AGENTS);

        string folder = Path.Combine(Path.GetTempPath(), "hex-no-ai-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "settings.hor");
            var written = new HexSettings(file) { NoAi = true };
            Assert.Null(written.Save());
            Assert.True(new HexSettings(file).NoAi);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("hex_agent_spawn", true)]
    [InlineData("hex_agent_hint", true)]
    [InlineData("hex_agent_approve", true)]
    [InlineData("hex_ask", true)]
    [InlineData("hex_plan_update", true)]
    [InlineData("hex_harness_stats", true)]
    [InlineData("hex_search", false)]
    [InlineData("hex_read", false)]
    [InlineData("hex_screenshot", false)]
    [InlineData("hex_terminal_run", false)]
    [InlineData("hex_dev_build", false)]
    public void No_ai_leaves_out_the_mcp_tools_about_agents_and_only_those(string tool, bool agents) =>
        Assert.Equal(agents, DawnScene.AgentsOnly(tool));
}
