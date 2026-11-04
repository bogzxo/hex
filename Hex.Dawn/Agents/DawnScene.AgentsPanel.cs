using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The Agents sidebar owns the provider selector, session history and composer.
// Rows render the transcript; Run launches the provider registered by its extension.
internal sealed partial class DawnScene
{
    private const string AGENTS_LAYOUT = "agents_view.hor";

    private Panel agentsView = null!;
    private StackPanel agentsChips = null!, agentsRows = null!;
    private ScrollPanel agentsScroll = null!;
    private Label agentsSubtitle = null!;
    private Panel agentsCompose = null!, agentsMeta = null!, agentsBody = null!;
    private TextBox agentsInput = null!;
    private Button agentsStop = null!, agentsSend = null!, agentsHistory = null!, agentsNew = null!;

    private readonly List<HexAgentChat> agentChats = [];
    private HexAgentChat? agentChat;
    private int agentChatsMade;

    // The agent picked for the next chat, by its profile's id, and whether the file on screen goes in front of what's asked
    private string? agentPicked;
    private bool agentWithFile = true;

    // What the view was last drawn from, it's drawn again when any of it moves
    private (int Chat, int Version) agentsDrawn = (-1, -1);
    private (string? Providers, int Width) agentsChipsDrawn;
    private bool agentsWasRunning;
    private float agentsWait;

    private void BuildAgentsPanel()
    {
        UILayout made = layout.Instantiate(AGENTS_LAYOUT, left);
        agentsView = made.Get<Panel>("agents_view");
        SideGrip(agentsView, UIResize.Right, left: true);
        agentsChips = made.Get<StackPanel>("agents_chips");
        agentsRows = made.Get<StackPanel>("agents_rows");
        agentsScroll = made.Get<ScrollPanel>("agents_scroll");
        agentsScroll.StickToEnd = true;
        agentsCompose = made.Get<Panel>("agents_compose");
        agentsMeta = made.Get<Panel>("agents_meta");
        agentsBody = made.Get<Panel>("agents_body");
        agentsSubtitle = made.Get<Label>("agents_subtitle");
        agentsInput = made.Get<TextBox>("agents_input");
        agentsStop = made.Get<Button>("agents_stop");
        agentsSend = made.Get<Button>("agents_send");
        agentsHistory = made.Get<Button>("agents_history");

        // Under the side's head with the rest of the views, where the tree would be
        left.MoveChild(agentsView, IndexIn(left, layout.Get<UIComponent>("side_tree")));

        agentsInput.Bare = true;
        agentsInput.OnSubmitted = _ => SendToAgent();
        agentsInput.OnChanged = value => { if (string.IsNullOrWhiteSpace(value)) agentDraftContext = null; };
        agentsSend.OnPressed = SendToAgent;
        agentsSend.Primary = true;
        agentsStop.Danger = true;
        agentsStop.OnPressed = () => StopAgent(agentChat);
        agentsHistory.OnPressed = () => ContextMenu.Show(chrome, agentsHistory.Bounds.PointAt(Origin.BottomLeft), ChatsMenu());
        agentsNew = made.Get<Button>("agents_new");
        agentsNew.OnPressed = NewAgentChat;
        BuildAgentsOptions(made);

        commands.Add("View/agent", Said("agents_panel.command"), ShowAgentsPanel, "Ctrl+Alt+A", "robot", UIKeyUse.BoundWhileTyping);
    }

    /// <summary>The Agents view of the side bar brought up, and the box given the keys.</summary>
    internal void ShowAgentsPanel()
    {
        left.Show(true);
        Side = HexSide.Agents;
        RebuildTree();
        treeDirty = false;
        agentsInput.Focus();
    }

    /// <summary>The view in the side bar shown or put away as the side bar's view changes, the tree away while it's up.</summary>
    private void PlaceAgentsView(bool shown)
    {
        if (shown)
        {
            sideTree.Visible = false;
            treeScroll.Visible = false;
            treeTitle.Text = Said("agents_panel.command");
        }

        if (agentsView.Visible == shown)
            return;

        agentsView.Visible = shown;
        if (shown)
        {
            agentsDrawn = (-1, -1);
            agentsChipsDrawn = default;
            agentRowsFor = default;
        }
    }

    /// <summary>Recent chats and commands for the selected conversation.</summary>
    private Menu ChatsMenu()
    {
        var menu = new Menu("agents chats");
        for (int i = agentChats.Count - 1; i >= 0; i--)
        {
            HexAgentChat chat = agentChats[i];
            menu.Add(chat.Title.Length > 0 ? chat.Title : Said("agents_panel.new_chat"), () =>
            {
                if (agentChat == chat) return;
                agentChat = chat;
                agentDraftContext = null;
                agentsInput.Text = string.Empty;
            }, icon: chat.Running ? "loading~spin" : "comment").IsChecked = () => chat == agentChat;
        }

        if (agentChat is not null)
        {
            menu.AddSeparator();
            menu.Add(Said("agents_panel.retry"), RetryAgent, icon: "refresh").IsEnabled = () => agentChat?.Running == false;
            menu.Add(Said("agents_panel.copy"), CopyAgentReply, icon: "copy");
            menu.Add(Said("agents_panel.clear"), ClearAgentChat, icon: "clear-all");
        }

        menu.Add(Said("agents_panel.saved"), ShowHarnessSaved, icon: "graph");

        return menu;
    }

    /// <summary>The agents that can be run from here, the extensions that said how, in the order they were said.</summary>
    private IEnumerable<HexAgentProfile> RunnableAgents() =>
        HexAgentProfiles.All.Where(profile => extensions.AgentRuns.ContainsKey(profile.Id));

    /// <summary>The agent a chat goes to, the one picked or the first there is.</summary>
    private string? AgentFor(HexAgentChat? chat) =>
        chat is { Entries.Count: > 0 } ? chat.Agent : agentPicked is { } picked && extensions.AgentRuns.ContainsKey(picked) ? picked : RunnableAgents().FirstOrDefault()?.Id;

    private void NewAgentChat()
    {
        agentChat = null;
        agentDraftContext = null;
        agentsInput.Text = string.Empty;
        agentsInput.Focus();
    }

    private void ClearAgentChat()
    {
        if (agentChat is not { } chat)
            return;

        StopAgent(chat);
        agentDraftContext = null;
        agentsInput.Text = string.Empty;
        agentChats.Remove(chat);
        agentChat = agentChats.LastOrDefault();
    }

    /// <summary>
    /// Every tick, the agents' lines taken in and handed to their extensions, and the view drawn again when what it shows
    /// moved. Only the lines are taken while the view isn't on screen.
    /// </summary>
    private void UpdateAgentsPanel(float dt)
    {
        TakeAgentLines();
        if (agentsView is null || !agentsView.Visible || !left.Shown)
            return;

        agentChat?.Tick();
        if ((agentsWait -= dt) > 0.0f)
            return;
        agentsWait = 0.1f;

        // Stop pops up in Send's place while the agent works, Send pops back when it's done
        bool running = agentChat?.Running == true;
        if (running != agentsWasRunning)
        {
            agentsWasRunning = running;
            agentsStop.Visible = running;
            agentsSend.Visible = !running;
            PopIn(running ? agentsStop : agentsSend);
        }

        agentsHistory.Enabled = agentChats.Count > 0;
        agentsNew.Enabled = agentChat is not null || agentsInput.Text.Length > 0 || agentDraftContext is not null;
        bool canCompose = CanComposeAgent();
        agentsSend.Enabled = canCompose && !string.IsNullOrWhiteSpace(agentsInput.Text);
        DrawAgentChips();
        DrawAgentOptions();
        TickRunningTimes();
        DrawAgentsHead();

        // The dots wait out a quiet moment by the clock, not by the chat changing
        if (agentChat is { Running: true } going && agentsDrawn == (going.Id, going.Version) && agentsTyping is null)
            ShowTyping(going);

        var now = (agentChat?.Id ?? 0, agentChat?.Version ?? -1);
        if (now != agentsDrawn || agentChat != agentRowsFor.Chat || AgentTranscriptWidth() != agentRowsFor.Width
            || !ReferenceEquals(compositor.Skin, agentRowsFor.Skin) || settings.UiAnimation != agentRowsFor.Motion)
        {
            agentsDrawn = now;
            DrawAgentRows();
        }
    }

    // The quiet line under the agents, the model, the tokens, the cost and the time of the turn, and the card at the
    // bottom in the theme's colours, its edge lit while the box has the keys
    private void DrawAgentsHead()
    {
        if (Skin is { } skin)
        {
            agentsCompose.Radius = skin.CornerRadius;
            agentsCompose.Color = skin.Palette.Field;
            agentsCompose.Outline = agentsInput.IsFocused ? skin.Palette.Accent with { W = 0.55f } : skin.BorderColor with { W = skin.BorderColor.W * 0.7f };
        }

        string? agent = AgentFor(agentChat);
        HexAgentProfile? profile = HexAgentProfiles.All.FirstOrDefault(each => each.Id == agent);

        // The agent's chip says who, so this is the rest of it, kept short for a side bar
        var parts = new List<string?> { AgentDraftCaption(), agentChat is null && profile is not null ? AgentState(profile) : null };
        if (agentChat is { } shown && (shown.InputTokens > 0 || shown.Model is not null || shown.Running || shown.TurnSeconds > 0))
        {
            parts.Add(shown.Model?.Replace("claude-", string.Empty));
            if (shown.InputTokens + shown.OutputTokens > 0)
                parts.Add(Said("agents_panel.tokens", ("input", Count(shown.InputTokens)), ("output", Count(shown.OutputTokens))).TrimStart(' ', '·'));
            if (shown.Cost > 0)
                parts.Add(Said("agents_panel.cost", ("cost", shown.Cost.ToString("0.000"))).TrimStart(' ', '·'));
            parts.Add($"{shown.TurnSeconds:0.0}s");
        }

        agentsSubtitle.Text = UIRichText.Literal(string.Join("  ·  ", parts.Where(part => !string.IsNullOrWhiteSpace(part))));
    }

    private bool CanComposeAgent() => AgentFor(agentChat) is { } id
        && extensions.AgentRuns.ContainsKey(id)
        && HexAgentProfiles.All.FirstOrDefault(profile => profile.Id == id) is { } profile
        && StateOf(profile) != AgentStates.Missing;

    private static string Count(long tokens) => tokens >= 10_000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString();
}
