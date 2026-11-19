using System.Numerics;
using System.Text;
using System.Text.Json;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The Agents view's provider chips, every agent system Hex can run with how it stands (ready, not installed, signed
// out), the one the next message goes to picked
internal sealed partial class DawnScene
{
    /// <summary>The provider selector, rebuilt when its choices, status or available width change.</summary>
    private void DrawAgentChips()
    {
        // What it was drawn from as a number, every agent that can run, which is picked and how each is, worked out with
        // nothing allocated. It was a string made again ten times a second, most of what the panel made standing still
        string? current = AgentFor(agentChat);
        IReadOnlyList<HexAgentProfile> all = HexAgentProfiles.All;
        long providers = 17;
        for (int i = 0; i < all.Count; i++)
        {
            HexAgentProfile agent = all[i];
            if (!extensions.AgentRuns.ContainsKey(agent.Id))
                continue;
            providers = providers * 31 + string.GetHashCode(agent.Id, StringComparison.Ordinal);
            providers = providers * 31 + (agent.Id == current ? 1 : 0) * 7 + (int)StateOf(agent);
        }
        float width = agentsView.Size.X;
        var key = (providers, (int)width);
        if (key == agentsChipsDrawn)
            return;
        HexAgentProfile[] agents = [.. RunnableAgents()];
        if (providers != agentsChipsDrawn.Providers && agentChat is not { Entries.Count: > 0 })
            agentRowsFor = default;
        agentsChipsDrawn = key;

        Empty(agentsChips);
        // A narrow sidebar gives the provider its own line. History and New stay at their
        // normal hit sizes on the next line beside the quiet status text.
        bool compact = width < 300;
        float available = Math.Max(80, width - (compact ? 0 : 80));
        agentsHistory.Position = new Vector2(-34, compact ? -32 : 0);
        agentsNew.Position = new Vector2(0, compact ? -32 : 0);
        // The harness goes down a line with them, and off the head where there's no room beside the turn's line for it.
        // Left where it was it sat over the provider. In Agentic it's the fleet's own line that says it
        bool harness = !compact || width >= 260;
        agentsHarness.Position = new Vector2(-68, compact ? -32 : 0);
        agentsHarness.Visible = harness && Face != Hex.Shell.HexFace.Noon;
        // Agentic's list has its own New over it, a second + over the chat said the same thing twice
        agentsNew.Visible = Face != Hex.Shell.HexFace.Noon;
        agentsMeta.Fill = UIFill.None;
        agentsMeta.Anchor = Origin.TopLeft;
        agentsMeta.Pivot = Origin.TopLeft;
        agentsMeta.Size = new Vector2(Math.Max(0, width - (compact ? (agentsHarness.Visible ? 104 : 70) : 0)), 20);
        agentsMeta.Position = new Vector2(0, compact ? -40 : -34);
        // In Agentic with Details beside it, the turn's line said again what Details says, the chat goes up into its room
        bool told = Face == Hex.Shell.HexFace.Noon && noonContext is { Visible: true };
        agentsMeta.Visible = !told;
        agentsHeadHeight = compact ? 74 : told ? 38 : 58;
        PlaceAgentsList();
        agentsChips.Size = new Vector2(available, 32);
        HexAgentProfile? picked = agents.FirstOrDefault(agent => agent.Id == current);
        string name = picked?.Name ?? Said("agents_panel.none_short");
        float labelScale = compact ? 0.125f : 0.14f;
        string icon = picked is null ? "robot" : IconOf(picked);
        string caption = name;
        // Measure the actual font and icons rather than guessing a character count.
        if (Skin is { } skin)
        {
            float room = available - skin.ButtonPadding.Total.X;
            while (caption.Length > 1 && skin.Font.Measure($"$({icon}) {UIRichText.Literal(caption)} $(chevron-down)", labelScale).X > room)
                caption = name[..(caption.EndsWith('…') ? caption.Length - 2 : caption.Length - 1)] + "…";
        }
        var selector = agentsChips.Add(new Button
        {
            Label = UIRichText.Literal(caption) + " $(chevron-down)",
            Icon = icon,
            Style = "button_flat", Align = Origin.Left, LabelScale = labelScale,
            Size = new Vector2(available, 32), Animated = false,
            Tooltip = picked is null ? name : $"{name}, {AgentState(picked)}"
        });
        selector.OnPressed = () =>
        {
            var menu = new Menu("agent provider");
            foreach (HexAgentProfile agent in agents)
            {
                string id = agent.Id;
                menu.Add($"{agent.Name} · {AgentState(agent)}", () =>
                {
                    agentPicked = id;
                    if (agentChat is { Entries.Count: > 0 } busy && busy.Agent != id) NewAgentChat();
                    agentsChipsDrawn = default;
                    agentRowsFor = default;
                    if (CanComposeAgent()) agentsInput.Focus();
                }, icon: IconOf(agent)).IsChecked = () => AgentFor(agentChat) == id;
            }
            if (agents.Length > 0) menu.AddSeparator();
            menu.Add(Said("agents_panel.browse"), BrowseAgentExtensions, icon: "extensions");
            ContextMenu.Show(chrome, selector.Bounds.PointAt(Origin.BottomLeft), menu);
        };
    }

    private void BrowseAgentExtensions() => OpenSettings(HexExtensions.AVAILABLE);

    private readonly Dictionary<string, (long Checked, string Program, bool Missing)> agentPrograms = [];
    private const long AGENT_PROGRAM_EVERY = 15_000;

    // What the looks on the pool found, by agent, waiting to be taken in on the editor's thread
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Program, bool Missing)> agentProgramsLooked = new();

    private enum AgentStates { Ready, Running, Missing }

    private AgentStates StateOf(HexAgentProfile agent)
    {
        // A loop rather than Any with a lambda, which made a closure every time the chips were looked at
        for (int i = 0; i < agentChats.Count; i++)
        {
            if (agentChats[i] is { Running: true } chat && chat.Agent == agent.Id)
                return AgentStates.Running;
        }
        if (!extensions.AgentRuns.TryGetValue(agent.Id, out var run)) return AgentStates.Missing;
        string program = HexExtensions.Text(run.Run, "program") ?? string.Empty;
        long now = System.Environment.TickCount64;
        // Looked for again every fifteen seconds, soon enough to see one installed meanwhile. Each look goes through every
        // folder of PATH and the system's PATH as it is now, and every two seconds it was most of what the panel made
        // A look that came back from the pool since, taken in
        if (agentProgramsLooked.TryRemove(agent.Id, out var looked) && agentPrograms.TryGetValue(agent.Id, out var had) && had.Program == looked.Program)
            agentPrograms[agent.Id] = had with { Missing = looked.Missing };

        if (!agentPrograms.TryGetValue(agent.Id, out var cached) || cached.Program != program)
        {
            // The first look is here and now, the panel has to say something
            cached = (now, program, HexAgentProcess.Find(program) is null);
            agentPrograms[agent.Id] = cached;
        }
        else if (now - cached.Checked > AGENT_PROGRAM_EVERY)
        {
            // Every look after on the pool, what it was standing until it comes back. Here it was a tick of 28 ms and 194 KB
            // every fifteen seconds with nothing else going on, the worst tick of an idle Hex
            agentPrograms[agent.Id] = cached with { Checked = now };
            string id = agent.Id;
            OffThread("looking for an agent's program", () =>
            {
                agentProgramsLooked[id] = (program, HexAgentProcess.Find(program) is null);
                Engine.WindowManager.RequestFrames();
            });
        }
        return cached.Missing ? AgentStates.Missing : AgentStates.Ready;
    }

    // Whether an agent can be asked, is busy, or isn't there to run, in words
    private string AgentState(HexAgentProfile agent) => StateOf(agent) switch
    {
        AgentStates.Running => Said("agents_panel.running"),
        AgentStates.Missing => Said("agents_panel.missing"),
        _ => Said("agents_panel.ready")
    };

    private string IconOf(HexAgentProfile agent) =>
        extensions.AgentRuns.TryGetValue(agent.Id, out var run) && HexExtensions.Text(run.Run, "icon") is { Length: > 0 } icon ? icon : "robot";
}
