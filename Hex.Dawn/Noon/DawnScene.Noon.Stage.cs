using System.Globalization;
using System.Numerics;

using Hex.Core.Git;
using Hex.Shell;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

/// <summary>What Agentic's middle shows, the chat on screen, or what of every agent waits on the person, or what the chat's agent changed, or its plan.</summary>
internal enum NoonStage
{
    Chat,
    Inbox,
    Changes,
    Plan
}

// Agentic's middle as tabs over it. Chat is the chat as ever. Inbox is everything of every agent that waits on the
// person, its requests to do something with Allow and Deny there, its questions with their answers, and the chats that
// finished or failed while they were elsewhere. Changes is what the chat's agent wrote, turn by turn, each file to see
// against how it was before the turn and to put back from the checkpoint Hex took as the turn began (HexCheckpoints).
// Plan is the to-do list the agent keeps as it works. Each is drawn again only when the agents moved, and the wheel over
// the margins scrolls whichever is up.
internal sealed partial class DawnScene
{
    // How tall the tabs over the middle are
    private const float NOON_TABS = 32.0f;

    private NoonStage noonStage = NoonStage.Chat;
    private StackPanel noonTabs = null!;
    private readonly Button[] noonTabButtons = new Button[4];
    private readonly Panel[] noonTabPanels = new Panel[4], noonTabLines = new Panel[4];
    private readonly Label[] noonTabKeys = new Label[4];
    private const float TAB_MIN = 60.0f, TAB_GAP = 10.0f;
    // What the tabs were last fitted to: the room, the counts, and the skin with its theme, text scale and accent, so a
    // switch of theme or font that leaves the width as it was still measures them again
    private (float Room, (int, int, int, int) Counts, UISkin? Skin, string? Theme, float Scale, Vector4 Accent, float Gui) noonTabsFitted = (-1, (-1, -1, -1, -1), null, null, 0, default, 0);

    /// <summary>
    /// The tabs sized to what's in them, the icon, the label with its count and the key, the keys left off when the strip
    /// is too narrow for all four with them. Fitted again as a count or the room moves.
    /// </summary>
    private void FitNoonTabs(float room)
    {
        if (Skin is not { } skin)
            return;
        var fitted = (room, noonTabsSaid, skin, skin.Theme, skin.TextScale, skin.AccentColor, compositor.Scale);
        if (noonTabsFitted == fitted)
            return;

        // The way the button measures itself: its icon as $(name) before the label at its scale, and the room its style
        // leaves round that, the art's own or the skin's button padding
        UIEdges padding = skin.TryGetRegion("button_flat", out var region) ? region.ContentOr(skin.ButtonPadding) : skin.ButtonPadding;
        Span<float> compact = stackalloc float[4];
        Span<float> expanded = stackalloc float[4];
        float withKeys = noonTabs.Spacing * (expanded.Length - 1);
        for (int i = 0; i < 4; i++)
        {
            Button button = noonTabButtons[i];
            float words = skin.Font.Measure($"$({button.Icon}) {button.Label}", button.LabelScale).X;
            float label = words + padding.Total.X;

            // The count's badge straight after the name, its room added to the tab
            Badge count = noonTabCounts[i];
            if (count.Visible)
            {
                float line = skin.Font.LineHeight * TAB_COUNT_SCALE;
                float badge = MathF.Ceiling(MathF.Max(skin.Font.Measure(count.Text, TAB_COUNT_SCALE, markup: false).X + 14.0f, line * 1.45f));
                count.Position = new Vector2(button.Padding.Left + words + TAB_COUNT_GAP, 0);
                label += badge + TAB_COUNT_GAP;
            }
            float hint = TAB_GAP + skin.Font.Measure(STAGE_KEYS[i], noonTabKeys[i].TextScale).X + 8.0f;
            compact[i] = MathF.Max(TAB_MIN, MathF.Ceiling(label));
            expanded[i] = MathF.Max(TAB_MIN, MathF.Ceiling(label + hint));
            withKeys += expanded[i];
        }

        bool keys = withKeys <= room;
        for (int i = 0; i < 4; i++)
        {
            float width = keys ? expanded[i] : compact[i];
            noonTabPanels[i].Size = new Vector2(width, 26);
            noonTabKeys[i].Visible = keys;
            noonTabLines[i].Size = new Vector2(MathF.Max(8, width - 16), 2);
            noonTabLines[i].Color = skin.AccentColor with { W = 1 };
        }

        noonTabsFitted = fitted;
    }
    private ScrollPanel noonInbox = null!, noonChanges = null!, noonPlan = null!;
    private StackPanel noonInboxRows = null!, noonChangesRows = null!, noonPlanRows = null!;

    // What the stage on screen was drawn from, and what the tabs say
    private long noonStageDrawn = NOT_DRAWN;

    // Which chat and tab the stage was last drawn for and when, and how often at the most it's drawn again for the same
    private int noonStageShown = -1;
    private long noonStageAt;
    private const long STAGE_REDRAW_MS = 400;
    private (int Inbox, int Changes, int Done, int All) noonTabsSaid = (-1, -1, -1, -1);

    // A tab's count as a badge after its name, the inbox's in the accent (it waits on you), the plan's green once it's all
    // done. They were the name and a number run together, "Changes  8"
    private readonly Badge[] noonTabCounts = new Badge[4];
    private const float TAB_COUNT_SCALE = 0.105f, TAB_COUNT_GAP = 6.0f;

    private static readonly string[] STAGE_KEYS = ["Alt+1", "Alt+2", "Alt+3", "Alt+4"];

    private void BuildNoonStage()
    {
        noonTabs = noonCenter.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Spacing = 2, Fill = UIFill.Horizontal, Size = new Vector2(0, NOON_TABS),
            Padding = new UIEdges(0, 0, 0, 6), Color = Vector4.Zero
        });
        // The fleet brought back from here while it's away
        noonFleetBack = noonTabs.Add(new Button
        {
            Icon = "layout-sidebar-left", Style = "button_flat", Size = new Vector2(30, 26), Animated = false, Visible = false,
            Tooltip = Said("noon.fleet_show", ("key", FLEET_KEY)), OnPressed = () => ShowFleet(true)
        });
        commands.Add("Agents/chats", Said("noon.command_fleet"), ToggleFleet, FLEET_KEY, "layout-sidebar-left", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        foreach (NoonStage stage in Enum.GetValues<NoonStage>())
        {
            NoonStage shown = stage;
            // The key dim at the tab's right, over the button so a press still lands on it, the keys were only in tooltips
            var tab = noonTabs.Add(new Panel { Size = new Vector2(TAB_MIN, 26), Color = Vector4.Zero });
            noonTabPanels[(int)stage] = tab;
            noonTabButtons[(int)stage] = tab.Add(new Button
            {
                Icon = StageIcon(stage), Label = TabLabel(stage, string.Empty), Style = "button_flat", Fill = UIFill.Both, Align = Origin.Left,
                LabelScale = 0.13f, Animated = false, Padding = new UIEdges(8, 0, 8, 0),
                Tooltip = Said("noon.stage_tip", ("name", Said($"noon.stage_{stage.ToString().ToLowerInvariant()}")), ("key", STAGE_KEYS[(int)stage])),
                OnPressed = () => ShowStage(shown)
            });
            noonTabCounts[(int)stage] = tab.Add(new Badge { Anchor = Origin.Left, Pivot = Origin.Left, TextScale = TAB_COUNT_SCALE, Visible = false });
            noonTabKeys[(int)stage] = tab.Add(new Label
            {
                Text = UIRichText.Literal(STAGE_KEYS[(int)stage]), TextScale = 0.11f, Dim = true, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0)
            });
            // The selected tab's mark, an accent line under it besides its fill
            noonTabLines[(int)stage] = tab.Add(new Panel { Anchor = Origin.Bottom, Pivot = Origin.Bottom, Size = new Vector2(TAB_MIN, 2), Radius = 1, Visible = false });
        }

        (noonInbox, noonInboxRows) = StagePanel();
        (noonChanges, noonChangesRows) = StagePanel();
        (noonPlan, noonPlanRows) = StagePanel();

        commands.Add("Agents/chats", Said("noon.command_stage_chat"), () => ShowStage(NoonStage.Chat), STAGE_KEYS[0], "comment-discussion", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_stage_inbox"), () => ShowStage(NoonStage.Inbox), STAGE_KEYS[1], "inbox", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_stage_changes"), () => ShowStage(NoonStage.Changes), STAGE_KEYS[2], "diff", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        commands.Add("Agents/chats", Said("noon.command_stage_plan"), () => ShowStage(NoonStage.Plan), STAGE_KEYS[3], "checklist", UIKeyUse.BoundWhileTyping).IsEnabled = InAgentic;
        // Only while a request waits, the keys are New Scratchpad in Language's the rest of the time (AnyRequestWaiting)
        commands.Add("Agents/chats", Said("noon.command_allow"), () => DecideWaiting(true), ALLOW_KEY, "check", UIKeyUse.BoundWhileTyping).IsEnabled = AnyRequestWaiting;
        commands.Add("Agents/chats", Said("noon.command_deny"), () => DecideWaiting(false), DENY_KEY, "close", UIKeyUse.BoundWhileTyping).IsEnabled = AnyRequestWaiting;
    }

    private bool InAgentic() => Face == HexFace.Noon;

    /// <summary>Whether any agent waits on the person, when Ctrl+Alt+Y and Ctrl+Alt+N answer it rather than doing what else they do.</summary>
    private bool AnyRequestWaiting()
    {
        foreach (HexAgentChat chat in agentChats)
            if (chat.HasPending)
                return true;
        return false;
    }

    // A tab's name, its count is a badge after it and its key a label of its own at the tab's right
    private string TabLabel(NoonStage stage, string count)
    {
        string name = Said($"noon.stage_{stage.ToString().ToLowerInvariant()}");
        return count.Length > 0 ? $"{name}  {count}" : name;
    }

    // A tab's count on its badge, hidden for none
    private void TabCount(NoonStage stage, string count, UIBadgeTone tone)
    {
        Badge badge = noonTabCounts[(int)stage];
        badge.Text = count;
        badge.Tone = tone;
        badge.Visible = count.Length > 0;
    }

    private static string StageIcon(NoonStage stage) => stage switch
    {
        NoonStage.Inbox => "inbox",
        NoonStage.Changes => "diff",
        NoonStage.Plan => "checklist",
        _ => "comment-discussion"
    };

    // A stage's scroll and the rows in it, over the middle in the chat's place
    private (ScrollPanel, StackPanel) StagePanel()
    {
        var scroll = noonCenter.Add(new ScrollPanel { Fill = UIFill.Horizontal, Visible = false, Padding = new UIEdges(2, 4, 6, 8) });
        var rows = scroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 8 });
        return (scroll, rows);
    }

    /// <summary>A stage of Agentic's middle on screen, drawn on the next tick.</summary>
    internal void ShowStage(NoonStage stage)
    {
        noonStage = stage;
        ApplyStage();
        noonStageDrawn = NOT_DRAWN;
        noonWait = 0.0f;
    }

    // The stage's panel up and the rest down, its tab marked
    private void ApplyStage()
    {
        if (Face != HexFace.Noon)
            return;
        agentsView.Visible = noonStage == NoonStage.Chat;
        noonInbox.Visible = noonStage == NoonStage.Inbox;
        noonChanges.Visible = noonStage == NoonStage.Changes;
        noonPlan.Visible = noonStage == NoonStage.Plan;
        for (int i = 0; i < noonTabButtons.Length; i++)
        {
            noonTabButtons[i].Selected = i == (int)noonStage;
            noonTabLines[i].Visible = i == (int)noonStage;
        }

        if (noonStage == NoonStage.Chat)
            agentsInput.Focus();
    }

    /// <summary>The scroll the wheel over the middle's margins goes to, the stage's that's up.</summary>
    private ScrollPanel? NoonWheeled() => noonStage switch
    {
        NoonStage.Inbox => noonInbox,
        NoonStage.Changes => noonChanges,
        NoonStage.Plan => noonPlan,
        _ => agentsScroll
    };

    /// <summary>The tabs' counts put right and the stage on screen drawn again when the agents moved, a few times a second.</summary>
    private void DrawNoonStage()
    {
        // The middle's width in it too, a stage's titles are fitted to it
        long key = ((hub.Version * 31 + (agentChat?.Id ?? -1)) * 31 + (int)noonStage) * 31 + (int)(noonCenter.Bounds.Width / 24);
        if (key == noonStageDrawn)
            return;
        // Another chat or tab is drawn at once. The agents only having gone on (one streaming moves it every word) waits a
        // little, and for a held button to be let go, an Allow made again under the press was lost
        long now = System.Environment.TickCount64;
        int shown = (agentChat?.Id ?? -1) * 8 + (int)noonStage;
        if (shown == noonStageShown && noonStageDrawn != NOT_DRAWN && (now - noonStageAt < STAGE_REDRAW_MS || Engine.Input.Mouse.IsDown(Horizon.Input.MouseButton.Left)))
            return;
        noonStageDrawn = key;
        noonStageShown = shown;
        noonStageAt = now;

        // Counted again only as the agents move, going through every turn's tool calls each tick allocated while nothing did
        HexAgentPlan? plan = HexAgentPlan.Of(agentChat);
        int done = plan?.Done ?? 0, all = plan?.Items.Count ?? 0;
        // The changes' count, the files the latest turn that wrote any wrote
        var counts = (InboxCount(), agentChat is null ? 0 : ChangedFilesOf(agentChat, latestOnly: true).FirstOrDefault().Files?.Count ?? 0, done, all);
        if (counts != noonTabsSaid)
        {
            noonTabsSaid = counts;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            // The inbox's count in the accent while something waits, it's what wants the person
            TabCount(NoonStage.Inbox, counts.Item1 > 0 ? counts.Item1.ToString(invariant) : string.Empty, UIBadgeTone.Accent);
            TabCount(NoonStage.Changes, counts.Item2 > 0 ? counts.Item2.ToString(invariant) : string.Empty, UIBadgeTone.Neutral);
            TabCount(NoonStage.Plan, all > 0 ? $"{done}/{all}" : string.Empty, all > 0 && done == all ? UIBadgeTone.Success : UIBadgeTone.Neutral);
        }

        switch (noonStage)
        {
            case NoonStage.Inbox:
                DrawInbox();
                break;
            case NoonStage.Changes:
                DrawChanges();
                break;
            case NoonStage.Plan:
                DrawPlan();
                break;
        }
    }

    /* Inbox */

    // How many things wait on the person, every agent's requests and questions and the chats to look at
    private int InboxCount()
    {
        int count = 0;
        for (int i = 0; i < agentChats.Count; i++)
        {
            HexAgentChat chat = agentChats[i];
            foreach (HexAgentEntry _ in chat.Pending)
                count++;
            if (!chat.Running && chat.Unread && chat != agentChat)
                count++;
        }

        return count;
    }

    private void DrawInbox()
    {
        ClearRows(noonInboxRows);
        bool any = false;
        foreach (HexAgentChat chat in FleetOrder())
        {
            foreach (HexAgentEntry asked in chat.Pending.ToArray())
            {
                any = true;
                if (asked.Kind == HexAgentEntryKind.Approval)
                    InboxApproval(chat, asked);
                else
                    InboxQuestion(chat, asked);
            }
        }

        foreach (HexAgentChat chat in FleetOrder())
        {
            if (chat.Running || !chat.Unread || chat == agentChat)
                continue;
            any = true;
            var card = InboxCard(chat, chat.Failed ? "error" : "pass", Said(chat.Failed ? "noon.inbox_failed" : "noon.inbox_done", ("name", ChatName(chat.Id))));
            card.Add(ContextText(OneLine(chat.LastReply(400), 300), dim: true));
            InboxButtons(card, chat, []);
        }

        if (!any)
            StageEmpty(noonInboxRows, "inbox", Said("noon.inbox_empty"));
    }

    /// <summary>
    /// A tab with nothing in it, what comes there and what can be done next. A dim line alone left the person wondering
    /// what to do, so the chat is always a key away and Plan can ask the agent for one.
    /// </summary>
    private void StageEmpty(StackPanel rows, string icon, string text, (string Label, string Icon, Action Then)? also = null)
    {
        rows.Add(new Label { Text = $"$({icon})", TextScale = 0.3f, Dim = true, Align = Origin.Left, Size = new Vector2(0, 34) });
        rows.Add(ContextText(text, dim: true));
        var buttons = rows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 28) });
        if (also is var (label, mark, then))
            buttons.Add(new Button { Label = label, Icon = mark, Primary = true, Size = new Vector2(MathF.Ceiling(label.Length * 6.6f) + 40, 26), LabelScale = 0.125f, Animated = false, OnPressed = then });
        string back = $"{Said("noon.stage_back")} ({STAGE_KEYS[(int)NoonStage.Chat]})";
        buttons.Add(new Button { Label = back, Icon = "comment-discussion", Size = new Vector2(MathF.Ceiling(back.Length * 6.6f) + 40, 26), LabelScale = 0.125f, Animated = false, OnPressed = () => ShowStage(NoonStage.Chat) });
    }

    // What asking an agent for a plan puts in its box, left there to be changed before it's sent
    private void AskForPlan()
    {
        ShowStage(NoonStage.Chat);
        agentsInput.Text = Said("noon.plan_ask_text");
        agentsInput.Focus();
    }

    // A card of the inbox, the agent's badge and who it is over what it is
    private StackPanel InboxCard(HexAgentChat chat, string icon, string title)
    {
        var card = noonInboxRows.Add(new StackPanel
        {
            Fill = UIFill.Horizontal, Spacing = 5, Padding = new UIEdges(12, 10, 12, 10), Radius = 8,
            Color = (Skin?.Palette.Field ?? new Vector4(0.1f, 0.1f, 0.12f, 1.0f)) with { W = 0.6f }, Outline = Skin?.BorderColor ?? Vector4.Zero
        });
        Vector4 tint = AgentTint(chat.Agent, Skin);
        card.Add(new Label
        {
            Text = $"[color:{HexColour(tint)}]$({icon})[/color] {UIRichText.Literal(title)}", TextScale = 0.14f, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal
        });
        return card;
    }

    // The buttons along a card's foot, its own and Open to go to the chat
    private void InboxButtons(StackPanel card, HexAgentChat chat, (string Label, string Icon, bool Primary, Action Pressed)[] own)
    {
        var row = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 26) });
        foreach (var (label, icon, primary, pressed) in own)
            row.Add(new Button { Label = label, Icon = icon, Primary = primary, Size = new Vector2(MathF.Max(80, label.Length * 7.0f + 34), 24), LabelScale = 0.125f, Animated = false, OnPressed = pressed });
        row.Add(new Button
        {
            Label = Said("noon.inbox_open"), Icon = "go-to-file", Style = "button_flat", Size = new Vector2(80, 24), LabelScale = 0.125f, Animated = false,
            OnPressed = () =>
            {
                OpenAgentChat(chat);
                ShowStage(NoonStage.Chat);
            }
        });
    }

    private void InboxApproval(HexAgentChat chat, HexAgentEntry asked)
    {
        var card = InboxCard(chat, "shield", Said("noon.inbox_asks", ("name", ChatName(chat.Id))));
        card.Add(ContextText(asked.Text, dim: false));
        if (!string.IsNullOrWhiteSpace(asked.Input))
            card.Add(ContextText(OneLine(asked.Input!, 400), dim: true)).Also(label => label.CodeFont = code.Font);
        var (allow, deny) = DecideLabels(asked);
        InboxButtons(card, chat,
        [
            (allow, "check", true, () => chat.Decide(asked, true)),
            (deny, "close", false, () => chat.Decide(asked, false))
        ]);
    }

    private void InboxQuestion(HexAgentChat chat, HexAgentEntry asked)
    {
        var card = InboxCard(chat, "question", Said("noon.inbox_question", ("name", ChatName(chat.Id))));
        card.Add(ContextText(asked.Text, dim: false));
        // Its answers a button each, the one it would pick marked. One that takes several, or an answer of the person's
        // own, is answered in its chat where the card has its toggles and its box
        var choices = new List<(string, string, bool, Action)>();
        string[] offered = asked.Choices ?? [];
        for (int i = 0; !asked.Multiple && i < offered.Length && i < 4; i++)
        {
            string choice = offered[i];
            choices.Add((choice, asked.Recommended == i ? "star-full" : "circle-large-outline", asked.Recommended == i, () => chat.Reply(asked, choice)));
        }
        InboxButtons(card, chat, [.. choices]);
    }

    /* Requests answered by key */

    // The keys for the request waiting, answered without the mouse from anywhere in Hex
    private const string ALLOW_KEY = "Ctrl+Alt+Y", DENY_KEY = "Ctrl+Alt+N";

    /// <summary>
    /// The request waiting answered, the open chat's oldest first, then the oldest of any chat in the fleet's order. Said
    /// so when nothing waits.
    /// </summary>
    private void DecideWaiting(bool allow)
    {
        if (KeyedRequest() is not var (asking, request))
        {
            Say(Said("noon.nothing_to_allow"));
            return;
        }

        asking.Decide(request, allow);
        // Which agent it was for said too, with several waiting the keys answered one the person couldn't tell from the rest
        Say($"{(allow ? Said("agents_panel.allowed") : Said("agents_panel.denied"))} {ChatName(asking.Id)}: {OneLine(request.Text, 80)}");
    }

    /// <summary>
    /// The request the keys answer, the open chat's oldest, then the oldest of any chat in the fleet's order. Its buttons
    /// are the only ones that say the keys, so it's plain which one they're for.
    /// </summary>
    private (HexAgentChat Chat, HexAgentEntry Request)? KeyedRequest()
    {
        HexAgentEntry? Oldest(HexAgentChat chat) => HexAgentHub.Approvals(chat).FirstOrDefault();
        HexAgentChat? asking = agentChat is { } open && Oldest(open) is not null ? open : FleetOrder().FirstOrDefault(chat => Oldest(chat) is not null);
        return asking is not null && Oldest(asking) is { } request ? (asking, request) : null;
    }

    // Allow and Deny for a request, saying their keys when they're the ones the keys answer
    private (string Allow, string Deny) DecideLabels(HexAgentEntry request) => KeyedRequest()?.Request == request
        ? (Said("noon.allow_key", ("key", ALLOW_KEY)), Said("noon.deny_key", ("key", DENY_KEY)))
        : (Said("agents_panel.allow"), Said("agents_panel.deny"));

    /* Plan */

    private void DrawPlan()
    {
        ClearRows(noonPlanRows);
        if (HexAgentPlan.Of(agentChat) is not { Items.Count: > 0 } plan)
        {
            StageEmpty(noonPlanRows, "checklist", Said("noon.plan_empty"), CanComposeAgent() ? (Said("noon.plan_ask"), "edit", AskForPlan) : null);
            return;
        }

        if (plan.Title is { Length: > 0 } title)
            noonPlanRows.Add(ContextText(title, dim: false));

        int done = plan.Done, all = plan.Items.Count;
        noonPlanRows.Add(ContextText(Said("noon.plan_progress", ("done", done.ToString(CultureInfo.InvariantCulture)), ("all", all.ToString(CultureInfo.InvariantCulture))), dim: true));
        // Steps done are good news the whole way, green, not the context's green to amber to red
        noonPlanRows.Add(ContextBar((float)done / all, MathF.Max(120, noonPlan.Bounds.Width - 30), Skin?.Palette.Success ?? new Vector4(0.35f, 0.75f, 0.45f, 1.0f)));

        // A plan the agent stopped on, steps still to do and none being done, waits for the person's go: asking for a plan
        // tells it to. Go says so in the chat, Change the plan puts words in the box to be finished
        if (agentChat is { Running: false } chat && plan.Items.Any(item => item.State == HexPlanState.Pending) && !plan.Items.Any(item => item.State == HexPlanState.Doing))
        {
            // Greyed out where nothing can be sent, an agent system not installed
            bool can = CanComposeAgent();
            noonPlanRows.Add(ContextText(Said("noon.plan_waiting"), dim: true));
            var go = noonPlanRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 30), Anchor = Origin.Left, Pivot = Origin.Left });
            go.Add(new Button
            {
                Label = Said("noon.plan_go"), Icon = "play", Primary = true, Enabled = can, Size = new Vector2(84, 28), LabelScale = 0.13f, Animated = false,
                OnPressed = () =>
                {
                    if (agentChat != chat || chat.Running)
                        return;
                    ShowStage(NoonStage.Chat);
                    agentsInput.Text = Said("noon.plan_go_text");
                    SendToAgent();
                }
            });
            go.Add(new Button
            {
                Label = Said("noon.plan_change"), Icon = "edit", Style = "button_flat", Enabled = can, Size = new Vector2(150, 28), LabelScale = 0.13f, Animated = false,
                OnPressed = () =>
                {
                    ShowStage(NoonStage.Chat);
                    agentsInput.Text = Said("noon.plan_change_text");
                    agentsInput.Focus();
                }
            });
        }
        string dim = HexColour(Skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
        foreach (HexPlanItem item in plan.Items)
        {
            string text = UIRichText.Literal(item.Text);
            string line = item.State switch
            {
                HexPlanState.Done => $"[color:{HexColour(Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f))}]$(pass-filled)[/color] [color:{dim}]{text}[/color]",
                HexPlanState.Doing => $"[color:{HexColour(AccentText(Skin))}]$(loading~spin)[/color] [b]{text}[/b]",
                HexPlanState.Blocked => $"$(warning) {text}",
                HexPlanState.Dropped => $"$(close) [color:{dim}]{text}[/color]",
                _ => $"$(circle-large-outline) {text}"
            };
            if (item.Agent is { } agent)
                line += $" [color:{dim}]#{agent}[/color]";
            if (item.Note is { Length: > 0 } note)
                line += $"\n[color:{dim}]{UIRichText.Literal(note)}[/color]";
            noonPlanRows.Add(new Label { Text = line, TextScale = 0.145f, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
        }
    }
}
