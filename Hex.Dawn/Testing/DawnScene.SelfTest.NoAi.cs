using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of No AI, run on its own (--selftest no-ai), which has the whole run without the agents since there's no
// taking them out of an editor made with them. Nothing of theirs was made: no Agentic on the title bar or behind it, no
// Agents view or robot on the activity bar, no Agents menu or commands of theirs in the palette, no agent systems among
// the extensions (the catalog's claude_code, codex and antigravity are there for the self test to find), none of their
// steps in the tick and none of the MCP tools about agents, the rest of the server's still there.
internal sealed partial class DawnScene
{
    private void TestNoAi(HexPointerScript test)
    {
        test.Check("no ai: the run is without the agents", () => HexAi.Off);

        test.Check("no ai: neither Agentic nor the Agents view was made", () => noonView is null && agentsView is null);
        test.Check("no ai: the title bar's faces are Dawn and Dusk, no Agentic among them", () =>
            modeToggle.Parent is { } row && row.Children.OfType<Button>().Select(button => button.Label).SequenceEqual([HexFace.Dawn.Name(), HexFace.Dusk.Name()]));
        test.Check("no ai: no robot on the activity bar", () => !layout.Get<Button>("act_agents").Visible);

        test.Check("no ai: no Agents menu on the bar", () => menu.Menus.All(bar => bar.Title != "Agents"));
        test.Check("no ai: none of the agents' commands in the palette", () =>
        {
            string[] theirs = [Said("agents_panel.command"), Said("noon.command"), Said("noon.command_new"), Said("agent.explain"), Said("agent.change"), Said("agents_panel.saved")];
            return PaletteChoices().All(choice => !theirs.Contains(Plain(choice.Text)) && !choice.Quiet.StartsWith("Agents", StringComparison.Ordinal));
        });

        test.Check("no ai: no agent system among the extensions, none of them said who its agent is", () =>
            extensions.All.All(extension => !Hex.Core.HexExtensionCatalog.IsAgentSystem(extension.Folder))
            && extensions.AgentRuns.Count == 0 && !HexAgentProfiles.All.Any());
        test.Check("no ai: the extensions that aren't agents are there as ever", () =>
            extensions.All.Any(extension => extension.Id == "designer"));

        test.Check("no ai: none of the agents' steps in the tick, the harness and the server's are", () =>
            TickNames.ToList() is var names && !names.Intersect(["panel.agent", "panel.agents_panel", "panel.noon", "panel.chats_kept"]).Any()
            && names.Contains("panel.harness") && names.Contains("panel.mcp"));

        test.Check("no ai: none of the MCP tools about agents, the ones for finding code and driving the editor all there", () =>
            McpToolList().Select(tool => tool.Meta.Name).ToList() is var tools
            && !tools.Any(AgentsOnly) && !tools.Any(tool => tool.StartsWith("hex_agent", StringComparison.Ordinal))
            && new[] { "hex_search", "hex_read", "hex_symbols", "hex_screenshot", "hex_run_command", "hex_terminal_run", "hex_edit" }.All(tools.Contains));

        // Asked for, Agentic isn't there to go to, and a side bar asked for the Agents view is on the explorer
        test.Run(() => ShowFace(HexFace.Noon));
        test.Check("no ai: asked for Agentic, the editor stays as it is", () => Face == HexFace.Dawn && body.Visible);
        test.Run(() => ShowSide(HexSide.Agents));
        test.Check("no ai: asked for the Agents view, the side bar shows the explorer", () => Side == HexSide.Explorer);

        // The setting's row is there, which is how No AI is switched for the next start
        test.Check("no ai: the settings have the No AI row under AI · Agents", () =>
            EditorSettingsCatalog.Settings.Any(setting => setting.Key == "no_ai" && setting.Group == EditorSettingsCatalog.AGENTS));
    }
}
