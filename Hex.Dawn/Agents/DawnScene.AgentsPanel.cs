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
    private CodeEditor agentsInput = null!;
    private Label agentsPlaceholder = null!;

    // The most lines the box grows to before it scrolls, and how tall the card under the chat is now
    private const int AGENTS_INPUT_LINES = 8;
    private float agentsComposeTall = 96.0f;
    private Button agentsStop = null!, agentsSend = null!, agentsHistory = null!, agentsNew = null!, agentsHarness = null!;
    private (long Calls, long Said) agentsHarnessShown = (-1, -1);

    // The agents' sessions, the model the panel, Noon and the tools all read, and their chats seen as a list
    private readonly HexAgentHub hub = new();
    private HexHubChats agentChats => hub.Chats;
    private HexAgentChat? agentChat;
    private int agentChatsMade;

    // The agent picked for the next chat, by its profile's id, and whether the file on screen goes in front of what's asked
    private string? agentPicked;
    private bool agentWithFile = true;

    // What the view was last drawn from, it's drawn again when any of it moves
    private (int Chat, int Version) agentsDrawn = (-1, -1);
    private (string? Providers, int Width) agentsChipsDrawn;
    private bool agentsWasRunning, agentsWasSteering;
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
        agentsPlaceholder = made.Get<Label>("agents_placeholder");
        agentsInput = agentsCompose.Add(new CodeEditor
        {
            Anchor = Origin.Top,
            Pivot = Origin.Top,
            Fill = UIFill.Horizontal,
            LineNumbers = false,
            WordWrap = true,
            StickyScroll = false,
            IndentGuides = false,
            Minimap = false,
            CheckSyntax = false,
            CaretBand = false,
            TextScale = 0.15f,
            FitRows = AGENTS_INPUT_LINES,
            Padding = new UIEdges(2, 2, 2, 2),
            Highlighter = CodeLanguages.ForFile("message.md"),
            Color = System.Numerics.Vector4.Zero,

            // No words offered back as it's typed, the list came up over the box and took Enter
            CompletionSource = _ => null
        });
        agentsCompose.MoveChild(agentsInput, 0);
        BuildAgentImages();
        agentsStop = made.Get<Button>("agents_stop");
        agentsSend = made.Get<Button>("agents_send");
        agentsHistory = made.Get<Button>("agents_history");
        agentsHarness = made.Get<Button>("agents_harness");
        agentsHarness.LabelScale = 0.11f;
        agentsHarness.Align = Origin.Left;
        agentsHarness.Animated = false;
        agentsHarness.OnPressed = ShowHarnessSaved;

        // Under the side's head with the rest of the views, where the tree would be
        left.MoveChild(agentsView, IndexIn(left, layout.Get<UIComponent>("side_tree")));

        // Enter sends, Shift+Enter is another line
        agentsInput.OnEnter = () =>
        {
            if (Engine.Input.Keyboard.Shift)
                return false;
            SendToAgent();
            return true;
        };
        agentsInput.OnChanged = () =>
        {
            if (string.IsNullOrWhiteSpace(agentsInput.Text))
                agentDraftContext = null;
        };
        agentsSend.OnPressed = SendToAgent;
        agentsSend.Primary = true;
        agentsStop.Danger = true;
        agentsStop.OnPressed = () => StopAgent(agentChat);
        agentsHistory.OnPressed = () => ContextMenu.Show(chrome, agentsHistory.Bounds.PointAt(Origin.BottomLeft), ChatsMenu());
        agentsNew = made.Get<Button>("agents_new");
        agentsNew.OnPressed = NewAgentChat;
        BuildAgentsOptions(made);
        BuildAgentsList();

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
        // Noon has it in its middle, the side bar's view changing is nothing to do with it then
        if (Face == HexFace.Noon)
            return;

        if (shown)
        {
            sideTree.Visible = false;
            treeScroll.Visible = false;
            treeTitle.Text = Said("agents_panel.command");
        }

        if (agentsView.Visible == shown)
            return;

        agentsView.Visible = shown;
        // Brought back it's drawn again at once, as it was left. The rows were made again as though the chat had just
        // been picked and came in one after another, a slow slide from the top every time the view was opened
        if (shown)
        {
            agentsDrawn = (-1, -1);
            agentsChipsDrawn = default;
            agentsStill = true;
        }
    }

    /// <summary>Recent chats and commands for the selected conversation.</summary>
    private Menu ChatsMenu()
    {
        var menu = new Menu("agents chats");
        for (int i = agentChats.Count - 1; i >= 0; i--)
        {
            HexAgentChat chat = agentChats[i];
            menu.Add(chat.Shown.Length > 0 ? chat.Shown : Said("agents_panel.new_chat"), () => OpenAgentChat(chat),
                icon: chat.Running ? "loading~spin" : "comment").IsChecked = () => chat == agentChat;
        }

        if (agentChat is not null)
        {
            menu.AddSeparator();
            menu.Add(Said("agents_panel.retry"), RetryAgent, icon: "refresh").IsEnabled = () => agentChat?.Running == false;
            menu.Add(Said("agents_panel.copy"), CopyAgentReply, icon: "copy");
            menu.Add(Said("agents_panel.clear"), ClearAgentChat, icon: "clear-all");
        }

        if (Face != HexFace.Noon)
            menu.Add(Said("agents_panel.chat_open_noon"), () => ShowFace(HexFace.Noon), icon: "robot");
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
        if (agentChat is { } chat)
            CloseAgentChat(chat);
    }

    /// <summary>
    /// Every tick, the agents' lines taken in and handed to their extensions, and the view drawn again when what it shows
    /// moved. Only the lines are taken while the view isn't on screen.
    /// </summary>
    private void UpdateAgentsPanel(float dt)
    {
        // A fold of the systems pressed is drawn on the ticks straight after, not when the panels next look
        if (systemsSettle > 0)
        {
            systemsSettle--;
            agentsWait = 0.0f;
            noonWait = 0.0f;
        }

        TakeAgentLines();
        PublishAgentStatuses();
        DrawAgentsActivity();
        if (agentsView is null || !agentsView.Visible || (!left.Shown && Face != HexFace.Noon))
            return;

        // The chat on screen is read as far as it's gone, so it's not marked as finished unseen
        if (agentChat is { } open)
        {
            open.Tick();
            open.Seen = open.Version;
        }
        // The rows and the cards follow the width every tick, a side being dragged moves every frame and the words fitted a
        // tenth of a second behind it jumped as they caught up. Each does nothing when the width didn't move
        FitAgentRows();
        RestoreAgentScroll();
        if ((int)agentsView.Size.X != chatRowsFitted)
        {
            chatRowsFitted = (int)agentsView.Size.X;
            foreach (Action<float> refit in chatRowFits)
                refit(agentsView.Size.X);
        }
        DrawAgentImages();
        FitAgentsCompose();

        if ((agentsWait -= dt) > 0.0f)
            return;
        agentsWait = 0.1f;

        // Stop pops up in Send's place while the agent works, Send pops back when it's done. An agent that can be told
        // more as it works keeps Send, Stop beside it
        bool running = agentChat?.Running == true;
        // Every running agent can be told more now, the ones that can't take it on their stdin by their mail
        bool steering = running;
        if (running != agentsWasRunning || steering != agentsWasSteering)
        {
            bool started = running != agentsWasRunning;
            agentsWasRunning = running;
            agentsWasSteering = steering;
            agentsStop.Visible = running;
            agentsSend.Visible = !running || steering;
            agentsStop.Position = new Vector2(steering ? -38 : 0, 0);
            if (started)
                PopIn(running ? agentsStop : agentsSend);
        }

        agentsHistory.Enabled = agentChats.Count > 0;
        agentsNew.Enabled = agentChat is not null || agentsInput.Text.Length > 0 || agentDraftContext is not null;
        bool canCompose = CanComposeAgent();
        agentsSend.Enabled = canCompose && (!string.IsNullOrWhiteSpace(agentsInput.Text) || agentImages.Count > 0);
        DrawAgentChips();
        DrawAgentsList();
        DrawAgentOptions();
        TickRunningTimes();
        DrawAgentsHead();
        DrawHarnessSaved();

        // The dots wait out a quiet moment by the clock, not by the chat changing
        if (agentChat is { Running: true } going && agentsDrawn == (going.Id, going.Version) && agentsTyping is null)
            ShowTyping(going);

        var now = (agentChat?.Id ?? 0, agentChat?.Version ?? -1);
        if (now != agentsDrawn || agentChat != agentRowsFor.Chat || (agentChat is { Entries.Count: > 0 } && AgentsNarrow()) != agentRowsFor.Narrow
            || !ReferenceEquals(compositor.Skin, agentRowsFor.Skin) || settings.UiAnimation != agentRowsFor.Motion)
        {
            agentsDrawn = now;
            DrawAgentRows();
        }
        else FitAgentRows();
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
        if (agentChat?.Parent is { } parent)
            parts.Add(Said("agents_panel.started_by", ("name", ChatName(parent))));
        if (agentChat is { } shown && (shown.InputTokens > 0 || shown.Model is not null || shown.Running || shown.TurnSeconds > 0))
        {
            parts.Add(shown.Model?.Replace("claude-", string.Empty));
            if (shown.InputTokens + shown.OutputTokens > 0)
                parts.Add(Said("agents_panel.tokens", ("input", Count(shown.InputTokens)), ("output", Count(shown.OutputTokens))).TrimStart(' ', '·'));
            if (shown.Cost > 0)
                parts.Add(Said("agents_panel.cost", ("cost", shown.Cost.ToString("0.000"))).TrimStart(' ', '·'));
            parts.Add(Said(shown.Running ? "agents_panel.tool_running" : "agents_panel.turn_done", ("seconds", shown.TurnSeconds.ToString("0.0"))));
        }

        agentsSubtitle.Text = UIRichText.Literal(string.Join("  ·  ", parts.Where(part => !string.IsNullOrWhiteSpace(part))));
        FitAgentsCompose();
    }

    /// <summary>
    /// The card at the bottom as tall as the box's lines and the pickers under them, the chat over it making room, and the
    /// placeholder while the box is empty.
    /// </summary>
    private void FitAgentsCompose()
    {
        // The skin paints an editor's ground over what it was given, the box is the card's
        agentsInput.Color = System.Numerics.Vector4.Zero;
        agentsPlaceholder.Visible = agentsInput.Text.Length == 0;
        float tall = MathF.Max(96.0f, MathF.Ceiling(agentsInput.Bounds.Height) + 56.0f) + AgentImagesTall;
        if (MathF.Abs(tall - agentsComposeTall) < 0.5f)
            return;
        agentsComposeTall = tall;
        agentsCompose.Size = agentsCompose.Size with { Y = tall };
        PlaceAgentsList();
    }

    /// <summary>
    /// What the harness has saved the agents this run, the tokens of what the plain way (grep, files read whole) would
    /// have had them read and didn't, and how much of it that was, put right only when it moved.
    /// </summary>
    private void DrawHarnessSaved()
    {
        var (calls, said, plain) = Harness.Stats.Totals();
        if ((calls, said) == agentsHarnessShown)
            return;
        agentsHarnessShown = (calls, said);

        agentsHarness.Label = calls == 0
            ? $"$(zap) {Said("agents_panel.harness_none")}"
            : $"$(zap) {Said("agents_panel.harness_saved", ("tokens", Count((plain - said) / 4)), ("share", plain <= 0 ? "0" : (100.0 * (plain - said) / plain).ToString("0")), ("calls", calls.ToString()))}";
    }

    // How many ticks a chat put on screen is laid out for before it goes back to where it was read to, its rows' heights
    // known only once they've been laid out
    private const int SCROLL_BACK_AFTER = 2;
    private int agentScrollTicks;

    /// <summary>The chat on screen back where it was read to, once its rows are laid out, the end when it was there.</summary>
    private void RestoreAgentScroll()
    {
        if (agentScrollWanted is not { } wanted)
        {
            agentScrollTicks = 0;
            return;
        }

        if (wanted.Chat != agentChat || wanted.Chat != agentRowsFor.Chat)
        {
            agentScrollWanted = null;
            return;
        }

        if (++agentScrollTicks < SCROLL_BACK_AFTER)
            return;
        agentScrollWanted = null;
        agentScrollTicks = 0;
        if (wanted.Offset is { } offset)
            agentsScroll.Offset = offset;
        else
            agentsScroll.ScrollToEnd();
    }

    private bool CanComposeAgent() => AgentFor(agentChat) is { } id
        && extensions.AgentRuns.ContainsKey(id)
        && HexAgentProfiles.All.FirstOrDefault(profile => profile.Id == id) is { } profile
        && StateOf(profile) != AgentStates.Missing;

    private static string Count(long tokens) => tokens >= 10_000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString();
}
