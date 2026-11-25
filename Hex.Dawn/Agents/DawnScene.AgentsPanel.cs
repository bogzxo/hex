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
    private StackPanel agentsChips = null!;
    private HexAgentRowList agentsRows = null!;
    private VirtualStack agentsScroll = null!;

    // What a row is taken to be tall before it's been measured, about a line of what an agent said and its gap
    private const float AGENTS_ROW_ESTIMATE = 40.0f;
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
    // Off until it's asked for, the file on screen is often nothing to do with what an agent is asked
    private bool agentWithFile;

    // What the view was last drawn from, it's drawn again when any of it moves
    private (int Chat, int Version) agentsDrawn = (-1, -1);
    private (long Providers, int Width) agentsChipsDrawn;
    private bool agentsWasRunning, agentsWasSteering;
    private float agentsWait;

    // Where the chat was read to as its width last moved, at its end or a share of the way down, and for how many more ticks
    private (bool AtEnd, float Share, int Ticks)? agentScrollHeld;
    private int agentScrollHeldWidth = -1;

    // The width the panel's parts were last drawn at and the room the systems took then, a change of either drawing them now
    private int agentsWidthDrawn = -1;
    private float agentsSystemsTallDrawn = -1.0f;

    private bool agentsPanelBuilt;

    /// <summary>Builds the Agents side-bar view and registers Agentic's commands once when the Agentic module is enabled.</summary>
    private void EnsureAgentsPanel()
    {
        if (agentsPanelBuilt || !AgenticEnabled)
            return;
        agentsPanelBuilt = true;
        BuildAgentsPanel();
        RegisterNoonCommands();
        HexAgentProcess.Woken = Engine.WindowManager.Wake;
    }

    /// <summary>Updates Agentic UI, ticks and activity button when <c>module_agentic</c> is toggled live in Settings.</summary>
    private void ApplyAgenticModule()
    {
        layout.Get<Button>("act_agents").Visible = AgenticEnabled;
        if (AgenticEnabled)
        {
            EnsureAgentsPanel();
            if (chatsKept is null)
                BringBackChats();
        }
        else
        {
            if (Face == HexFace.Noon)
                ShowFace(HexFace.Dawn);
            if (Side == HexSide.Agents)
                Side = HexSide.Explorer;
        }
        DeclareTicks();
    }

    private void BuildAgentsPanel()
    {
        UILayout made = layout.Instantiate(AGENTS_LAYOUT, left);
        agentsView = made.Get<Panel>("agents_view");
        SideGrip(agentsView, UIResize.Right, left: true);
        agentsChips = made.Get<StackPanel>("agents_chips");
        agentsCompose = made.Get<Panel>("agents_compose");
        agentsMeta = made.Get<Panel>("agents_meta");
        agentsBody = made.Get<Panel>("agents_body");

        // The layout's scroll and stack give way to a list that has only the rows near what's in sight in its tree, the
        // same room, padding and gap between rows
        ScrollPanel laid = made.Get<ScrollPanel>("agents_scroll");
        StackPanel laidRows = made.Get<StackPanel>("agents_rows");
        agentsScroll = agentsBody.Insert(IndexIn(agentsBody, laid), new VirtualStack
        {
            Fill = laid.Fill, Anchor = laid.Anchor, Pivot = laid.Pivot, Padding = laid.Padding,
            Spacing = laidRows.Spacing >= 0.0f ? laidRows.Spacing : 8.0f, Estimate = AGENTS_ROW_ESTIMATE, StickToEnd = true
        });
        agentsBody.Remove(laid);
        agentsRows = new HexAgentRowList(agentsScroll)
        {
            MakeLater = (of, place) => MadeLater(agentRowsFor.Chat!, (HexAgentEntry)of, place)
        };
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
            // Two rows at the least, the card's room to write in all of it the box, a click anywhere there landing in it
            FitLeast = 2,
            Padding = new UIEdges(2, 2, 2, 2),
            Highlighter = CodeLanguages.ForFile("message.md"),
            Color = System.Numerics.Vector4.Zero,

            // No words offered back as it's typed, the list came up over the box and took Enter
            CompletionSource = _ => null
        });
        agentsInput.OnContextMenu = point => ShowSpellingMenu(agentsInput, point);
        agentsCompose.MoveChild(agentsInput, 0);
        BuildAgentImages();

        // The rows a chat no longer keeps in memory, in Hex's data folder, a self test's in a folder of its own it leaves behind
        hub.History = new HexAgentHistory(options.SelfTest
            ? Path.Combine(Path.GetTempPath(), "hex-selftest-history-" + System.Environment.ProcessId)
            : Hex.Core.HexData.AgentHistory);
        hub.History.Tidy(TimeSpan.FromDays(2));
        // What an agent another started asks leave to do goes to that one too (DawnScene.McpAgentApprovals)
        hub.ApprovalAsked = RouteApproval;
        agentsStop = made.Get<Button>("agents_stop");
        agentsSend = made.Get<Button>("agents_send");
        agentsHistory = made.Get<Button>("agents_history");
        agentsHarness = made.Get<Button>("agents_harness");
        agentsHarness.Animated = false;
        agentsHarness.OnPressed = ShowHarnessSaved;

        // Under the side's head with the rest of the views, where the tree would be
        left.MoveChild(agentsView, IndexIn(left, layout.Get<UIComponent>("side_tree")));

        // Enter sends, Shift+Enter is another line
        agentsInput.OnEnter = () =>
        {
            if (Engine.Input.Keyboard.Shift)
                return false;
            if (TakeFirstSlash(sending: true))
                return true;
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

        commands.Add("Agents/open", Said("agents_panel.command"), ShowAgentsPanel, "Ctrl+Alt+A", "robot", UIKeyUse.BoundWhileTyping);
    }

    /// <summary>Whether the optional Agentic module is enabled this run (respects both <c>module_agentic</c> and <see cref="HexAi.Off"/>).</summary>
    private static bool AgenticEnabled => Hex.Core.Modules.HexModules.IsEnabled(Hex.Core.Modules.HexModuleId.Agentic);

    /// <summary>The Agents view of the side bar brought up, and the box given the keys.</summary>
    internal void ShowAgentsPanel()
    {
        if (!AgenticEnabled)
        {
            Say(Said("no_ai.no_agentic"));
            return;
        }
        EnsureAgentsPanel();
        left.Show(true);
        Side = HexSide.Agents;
        RebuildTree();
        treeDirty = false;
        agentsInput?.Focus();
    }

    /// <summary>The view in the side bar shown or put away as the side bar's view changes, the tree away while it's up.</summary>
    private void PlaceAgentsView(bool shown)
    {
        // Noon has it in its middle, the side bar's view changing is nothing to do with it then
        if (Face == HexFace.Noon)
            return;

        if (!shown && agentsView is null)
            return;
        if (shown && !AgenticEnabled)
            return;
        if (shown && agentsView is null)
            EnsureAgentsPanel();
        if (agentsView is null)
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

    // How many of the newest chats the chats menu lists itself, the older ones are in a menu off it
    private const int RECENT_CHATS_SHOWN = 10;

    /// <summary>Recent chats and commands for the selected conversation.</summary>
    private Menu ChatsMenu()
    {
        var menu = new Menu("agents chats");

        // The newest chats, the rest in a menu of their own off the last, a list of every chat ran off the window
        Menu older = menu;
        for (int i = agentChats.Count - 1, shown = 0; i >= 0; i--, shown++)
        {
            if (shown == RECENT_CHATS_SHOWN)
                older = menu.AddMenu(Said("agents_panel.older_chats"), "history");

            HexAgentChat chat = agentChats[i];
            older.Add(chat.Shown.Length > 0 ? chat.Shown : Said("agents_panel.new_chat"), () => OpenAgentChat(chat),
                icon: chat.Running ? "loading~spin" : "comment").IsChecked = () => chat == agentChat;
        }

        if (agentChat is not null)
        {
            menu.AddSeparator();
            menu.Add(Said("agents_panel.retry"), RetryAgent, icon: "refresh").IsEnabled = () => agentChat?.Running == false;
            menu.Add(Said("agents_panel.copy"), CopyAgentReply, icon: "copy");
        }

        menu.AddSeparator();
        if (Face != HexFace.Noon)
            menu.Add(Said("agents_panel.chat_open_noon"), () => ShowFace(HexFace.Noon), icon: "robot");
        menu.Add(Said("agents_panel.saved"), ShowHarnessSaved, icon: "graph");

        // Emptying the chat goes last, in the error colour, it can't be had back
        if (agentChat is not null)
        {
            menu.AddSeparator();
            menu.Add(Said("agents_panel.clear"), ClearAgentChat, icon: "clear-all", destructive: true);
        }

        return menu;
    }

    /// <summary>The agents that can be run from here, the extensions that said how, in the order they were said.</summary>
    private IEnumerable<HexAgentProfile> RunnableAgents() =>
        HexAgentProfiles.All.Where(profile => extensions.AgentRuns.ContainsKey(profile.Id));

    /// <summary>The agent a chat goes to, the one picked or the first there is. Zero allocations on the hot path.</summary>
    private string? AgentFor(HexAgentChat? chat)
    {
        if (chat is { Entries.Count: > 0 })
            return chat.Agent;
        if (agentPicked is { } picked && extensions.AgentRuns.ContainsKey(picked))
            return picked;
        IReadOnlyList<HexAgentProfile> all = HexAgentProfiles.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (extensions.AgentRuns.ContainsKey(all[i].Id))
                return all[i].Id;
        }
        return null;
    }

    private static HexAgentProfile? FindAgentProfile(string id)
    {
        IReadOnlyList<HexAgentProfile> all = HexAgentProfiles.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Id == id)
                return all[i];
        }
        return null;
    }

    private void NewAgentChat()
    {
        agentChat = null;
        agentDraftContext = null;
        if (agentsInput is not null)
            agentsInput.Text = string.Empty;
        // Begun from a narrow window's fleet, the middle comes back before the box takes the keys
        FleetPicked();
        agentsInput?.Focus();
    }

    private void ClearAgentChat()
    {
        if (agentChat is { } chat)
            AskCloseAgentChat(chat);
    }

    /// <summary>
    /// Every tick, the agents' lines taken in and handed to their extensions, and the view drawn again when what it shows
    /// moved. Only the lines are taken while the view isn't on screen.
    /// </summary>
    private void UpdateAgentsPanel(float dt)
    {
        if (!AgenticEnabled)
            return;
        // A frame's length while the agentic perf test measures
        perfFrames?.Add(dt);
        // A fold of the systems pressed is drawn on the ticks straight after, not when the panels next look
        if (systemsSettle > 0)
        {
            systemsSettle--;
            agentsWait = 0.0f;
            noonWait = 0.0f;
        }

        TakeAgentLines();
        // The systems panels' heights eased a step every frame, the columns they're in following
        EaseSystems(Face == HexFace.Noon ? noonSystems : agentsSystems, dt);
        PublishAgentStatuses();
        ExpireQuestions();
        DrawAgentsActivity();
        if (agentsView is null || !agentsView.Visible || (!left.Shown && Face != HexFace.Noon))
            return;

        // The chat on screen is read as far as it's gone, so it's not marked as finished unseen
        if (agentChat is { } open)
        {
            open.Tick();
            open.Seen = open.Version;
        }
        // The place the chat is read to is held while its width moves, the rows growing and shrinking as their words wrap
        // anew. At its end it stays at its end, elsewhere at the same share of the way down. Held by its pixels it looked
        // scrolled with every step of the drag and snapped back once the drag let go. Kept a few ticks, the rows' heights
        // are known only once they're laid out
        // The panel's own width, not the text's, which also moves as a scroll bar comes or goes going between chats, where
        // the place each chat was read to is put back instead (RestoreAgentScroll)
        // Only while a side's edge is being dragged, the view moving between the side bar and Agentic's middle, or a chat
        // made again, being new places rather than a drag
        int panelWidth = (int)agentsView.Size.X;
        bool resized = panelWidth != agentScrollHeldWidth && (SideIsResizing(leftGrips) || SideIsResizing(rightGrips));
        agentScrollHeldWidth = panelWidth;
        if (agentScrollWanted is not null || agentChat != agentRowsFor.Chat)
            agentScrollHeld = null;
        else if (resized && agentsScroll.MaxOffset > 0)
        {
            // Taken as the width starts to move, then kept for as long as it goes on moving
            agentScrollHeld = agentScrollHeld is { Ticks: > 0 } holding
                ? holding with { Ticks = 4 }
                : (agentsScroll.Offset >= agentsScroll.MaxOffset - 1.0f, agentsScroll.Offset / agentsScroll.MaxOffset, 4);
        }

        if (agentScrollHeld is { Ticks: > 0 } held && agentsScroll.MaxOffset > 0)
        {
            if (held.AtEnd)
                agentsScroll.ScrollToEnd();
            else
                agentsScroll.Offset = MathF.Round(held.Share * agentsScroll.MaxOffset);
            agentScrollHeld = held with { Ticks = held.Ticks - 1 };
        }

        KeepHeldRow();
        LoadEarlierAtTop();

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
        DrawAgentsLatest();
        DrawAgentSlash();
        DrawAgentMention();

        // The rest follows the width as well on the tick it moves, the list over the chat, the systems under it, the
        // agents' chips and the pickers. Drawn a tenth of a second behind a side being dragged, each jumped as it caught up,
        // and the chat under them jumped with them. The room the systems take is known a frame after they're laid out, so
        // that moving counts too
        float systemsTall = SystemsTall(agentsSystems);
        if ((int)agentsView.Size.X != agentsWidthDrawn || systemsTall != agentsSystemsTallDrawn)
        {
            agentsWidthDrawn = (int)agentsView.Size.X;
            agentsSystemsTallDrawn = systemsTall;
            agentsWait = 0.0f;
        }

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
        bool hasInput = !AgentsInputEmpty();
        agentsNew.Enabled = agentChat is not null || hasInput || agentDraftContext is not null;
        bool canCompose = CanComposeAgent();
        agentsSend.Enabled = canCompose && ((hasInput && !string.IsNullOrWhiteSpace(agentsInput.Text)) || agentImages.Count > 0);
        DrawAgentChips();
        DrawAgentsList();
        DrawAgentOptions();
        TickRunningTimes();
        DrawAgentsHead();
        DrawAgentsWaiting();
        DrawLinkBanner();
        DrawHarnessSaved();

        // The dots wait out a quiet moment by the clock, not by the chat changing
        if (agentChat is { Running: true } going && agentsDrawn == (going.Id, going.Version) && agentsTyping is not { Children: [{ Visible: true }, ..] })
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

    // What the line under the agents was last written from
    private (HexAgentChat? Chat, int Version, int Tenths, object? Draft, string? Agent, int State) agentsHeadDrawn = (null, -2, -2, null, null, -2);
    private readonly System.Text.StringBuilder agentsHeadBuilder = new(128);

    /// <summary>Checks whether the composer is empty without materializing a joined string from its document.</summary>
    private bool AgentsInputEmpty() =>
        agentsInput is null || (agentsInput.Document.LineCount <= 1 && agentsInput.Document.LineAt(0).Length == 0);

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
        HexAgentProfile? profile = null;
        IReadOnlyList<HexAgentProfile> all = HexAgentProfiles.All;
        for (int i = 0; i < all.Count && profile is null; i++)
        {
            if (all[i].Id == agent)
                profile = all[i];
        }

        // Its words made again only when what they say moved, the running turn's clock by the tenth. Ten times a second
        // they were all made anew, the most the panel still made standing still
        var now = (agentChat, agentChat?.Version ?? -1, agentChat is { Running: true } going ? (int)(going.TurnSeconds * 10) : -1,
            agentDraftContext, agent, profile is null ? -1 : (int)StateOf(profile));
        if (now == agentsHeadDrawn)
        {
            FitAgentsCompose();
            return;
        }
        agentsHeadDrawn = now;

        // The agent's chip says who, so this is the rest of it, kept short for a side bar
        agentsHeadBuilder.Clear();
        static void AppendPart(System.Text.StringBuilder sb, string? part)
        {
            if (string.IsNullOrWhiteSpace(part))
                return;
            if (sb.Length > 0)
                sb.Append("  ·  ");
            sb.Append(part);
        }

        AppendPart(agentsHeadBuilder, AgentDraftCaption());
        if (agentChat is null && profile is not null)
            AppendPart(agentsHeadBuilder, AgentState(profile));
        if (agentChat?.Parent is { } parent)
            AppendPart(agentsHeadBuilder, Said("agents_panel.started_by", ("name", ChatName(parent))));
        if (agentChat is { } shown && (shown.InputTokens > 0 || shown.Model is not null || shown.Running || shown.TurnSeconds > 0))
        {
            AppendPart(agentsHeadBuilder, HexAgentModels.Of(shown));
            if (shown.InputTokens + shown.OutputTokens > 0)
                AppendPart(agentsHeadBuilder, Said("agents_panel.tokens", ("input", Count(shown.InputTokens)), ("output", Count(shown.OutputTokens))).TrimStart(' ', '·'));
            if (shown.Cost > 0)
                AppendPart(agentsHeadBuilder, Said("agents_panel.cost", ("cost", shown.Cost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))).TrimStart(' ', '·'));
            AppendPart(agentsHeadBuilder, Said(shown.Running ? "agents_panel.tool_running" : "agents_panel.turn_done", ("seconds", shown.TurnSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));
        }

        agentsSubtitle.Text = UIRichText.Literal(agentsHeadBuilder.ToString());
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
        agentsPlaceholder.Visible = AgentsInputEmpty();
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

        string saved = calls == 0
            ? Said("agents_panel.harness_none")
            : Said("agents_panel.harness_saved", ("tokens", Count((plain - said) / 4)), ("share", plain <= 0 ? "0" : (100.0 * (plain - said) / plain).ToString("0")), ("calls", calls.ToString()));
        agentsHarness.Tooltip = $"{saved}\n{Said("agents_panel.harness_tip")}";
        if (noonHarness is not null)
            noonHarness.Label = $"$(zap) {saved}";
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

    private bool CanComposeAgent() => LinkCanCompose() ?? (AgentFor(agentChat) is { } id
        && extensions.AgentRuns.ContainsKey(id)
        && FindAgentProfile(id) is { } profile
        && StateOf(profile) != AgentStates.Missing);

    private static string Count(long tokens) => HexAgentLimits.Tokens(tokens);
}
