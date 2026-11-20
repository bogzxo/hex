using System.Globalization;
using System.Numerics;
using System.Text;

using Hex.Shell;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

/// <summary>Which of a plan's steps the Plan tab lists.</summary>
internal enum NoonPlanFilter
{
    // What's going, what's stuck and what's next, the steps done folded under them
    Open,
    Blocked,
    // Newest first, the dropped ones after them
    Done,
    // Every step in the plan's order
    All
}

// Agentic's Plan tab as a plan manager rather than a log. A long session left dozens of done rows over the one step going
// on, so the tab reads from what's now: a head with the plan's title, how far it got and its counts, then the step going
// on, the blocked ones with their reasons (they want the person), the steps next in order and a box to add one. The steps
// done fold into one row, newest first when opened, and an earlier task's steps (put aside when the agent began a new
// plan, or the person cleared what was done) fold under that. Dropped steps show only under Done and All.
//
// The person can manage it too: a step's row opens its details (the note, who has it, its files) with its next move, and
// its menu marks it, edits it, moves it or drops it. The plan is the agent's as well, so what the person changed is told
// to it, with their next message or, while it works, as a message it takes in (hex_plan_read shows it the plan).
internal sealed partial class DawnScene
{
    private NoonPlanFilter noonPlanFilter = NoonPlanFilter.Open;
    private bool noonPlanDoneShown, noonPlanEarlierShown;

    // The step with its details out, for the chat it's of
    private string? noonPlanOpened;
    private int noonPlanOpenedChat = -1;

    // The box adding a step, kept across redraws so what's being typed isn't lost as the agents move
    private TextBox? noonPlanAdd;

    // What the person changed of each chat's plan and not yet told its agent, and when they last did
    private readonly Dictionary<int, List<string>> planTold = [];
    private long planToldAt;
    private const long PLAN_TOLD_QUIET_MS = 1500;

    private const float PLAN_ROW = 28.0f, PLAN_ROW_NOTE = 46.0f, PLAN_TEXT = 0.14f, PLAN_SMALL = 0.125f;

    /// <summary>The Plan tab drawn again on the next tick, at once rather than after the few-times-a-second wait.</summary>
    private void RedrawPlan()
    {
        noonStageDrawn = NOT_DRAWN;
        noonStageShown = -1;
    }

    private void DrawPlan()
    {
        ClearRows(noonPlanRows);
        HexAgentChat? chat = agentChat;
        if (HexAgentPlan.Of(chat) is not { } plan || (plan.Items.Count == 0 && plan.Earlier.Count == 0))
        {
            StageEmpty(noonPlanRows, "checklist", Said("noon.plan_empty"), CanComposeAgent() ? (Said("noon.plan_ask"), "edit", AskForPlan) : null);
            return;
        }

        if (noonPlanOpenedChat != (chat?.Id ?? -1))
        {
            noonPlanOpened = null;
            noonPlanOpenedChat = chat?.Id ?? -1;
        }

        int doing = 0, blocked = 0, pending = 0, done = 0, dropped = 0;
        foreach (HexPlanItem item in plan.Items)
        {
            switch (item.State)
            {
                case HexPlanState.Doing: doing++; break;
                case HexPlanState.Blocked: blocked++; break;
                case HexPlanState.Done: done++; break;
                case HexPlanState.Dropped: dropped++; break;
                default: pending++; break;
            }
        }

        PlanHead(chat, plan, doing, blocked, pending, done, dropped);
        PlanWaiting(chat, plan, doing, pending);
        PlanFilters(doing + blocked + pending, blocked, done + dropped, plan.Items.Count);

        switch (noonPlanFilter)
        {
            case NoonPlanFilter.Blocked:
                if (blocked == 0)
                    PlanNothing(Said("noon.plan_none_blocked"));
                foreach (HexPlanItem item in plan.Items)
                    if (item.State == HexPlanState.Blocked)
                        PlanRow(noonPlanRows, chat, plan, item, null);
                break;
            case NoonPlanFilter.Done:
                if (done + dropped == 0)
                    PlanNothing(Said("noon.plan_none_done"));
                foreach (HexPlanItem item in DoneNewestFirst(plan.Items))
                    PlanRow(noonPlanRows, chat, plan, item, null);
                for (int i = plan.Items.Count - 1; i >= 0; i--)
                    if (plan.Items[i].State == HexPlanState.Dropped)
                        PlanRow(noonPlanRows, chat, plan, plan.Items[i], null);
                PlanEarlier(plan);
                break;
            case NoonPlanFilter.All:
                for (int i = 0; i < plan.Items.Count; i++)
                    PlanRow(noonPlanRows, chat, plan, plan.Items[i], null);
                PlanAddBox(chat);
                PlanEarlier(plan);
                break;
            default:
                PlanOpen(chat, plan, doing, blocked, pending, done);
                break;
        }
    }

    // What's open: the step going on leading, the blocked ones wanting the person, then what's next and the steps done folded
    private void PlanOpen(HexAgentChat? chat, HexAgentPlan plan, int doing, int blocked, int pending, int done)
    {
        if (doing > 0)
        {
            PlanSection(Said("noon.plan_now"), doing);
            foreach (HexPlanItem item in plan.Items)
                if (item.State == HexPlanState.Doing)
                    PlanRow(noonPlanRows, chat, plan, item, null);
        }

        if (blocked > 0)
        {
            PlanSection(Said("noon.plan_attention"), blocked);
            foreach (HexPlanItem item in plan.Items)
                if (item.State == HexPlanState.Blocked)
                    PlanRow(noonPlanRows, chat, plan, item, null);
        }

        PlanSection(Said("noon.plan_next"), pending);
        int number = 0;
        foreach (HexPlanItem item in plan.Items)
            if (item.State == HexPlanState.Pending)
                PlanRow(noonPlanRows, chat, plan, item, ++number);
        if (doing + blocked + pending == 0)
            PlanNothing(Said("noon.plan_none_open"));
        PlanAddBox(chat);

        if (done > 0)
        {
            PlanFold(Said("noon.plan_done_fold", ("count", done.ToString(CultureInfo.InvariantCulture))), "pass", noonPlanDoneShown, () => noonPlanDoneShown = !noonPlanDoneShown);
            if (noonPlanDoneShown)
                foreach (HexPlanItem item in DoneNewestFirst(plan.Items))
                    PlanRow(noonPlanRows, chat, plan, item, null);
        }

        PlanEarlier(plan);
    }

    // The steps done, the last done first, those done before times were kept by their place, the later in the plan first
    private static IEnumerable<HexPlanItem> DoneNewestFirst(IReadOnlyList<HexPlanItem> items) => items
        .Select((item, index) => (item, index)).Where(each => each.item.State == HexPlanState.Done)
        .OrderByDescending(each => each.item.DoneAt).ThenByDescending(each => each.index).Select(each => each.item);

    // The head of the plan, a card with its title, how far it got as a bar and its counts as badges, and its menu
    private void PlanHead(HexAgentChat? chat, HexAgentPlan plan, int doing, int blocked, int pending, int done, int dropped)
    {
        UISkin? skin = Skin;
        var card = noonPlanRows.Add(PlanCard(skin?.BorderColor ?? Vector4.Zero));
        var top = card.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 26), Color = Vector4.Zero });
        string title = plan.Title is { Length: > 0 } named ? named : Said("noon.plan_untitled");
        top.Add(new Label
        {
            Text = UIRichText.Literal(Fit(title, PlanWidth() - 70, 0.17f)),
            TextScale = 0.17f,
            Bold = true,
            Align = Origin.Left,
            Anchor = Origin.Left,
            Pivot = Origin.Left,
            Tooltip = title
        });
        var menu = top.Add(new Button
        {
            Icon = "ellipsis",
            Style = "button_flat",
            Size = new Vector2(26, 24),
            Anchor = Origin.Right,
            Pivot = Origin.Right,
            Animated = false,
            Tooltip = Said("noon.plan_menu_tip")
        });
        menu.OnPressed = () => ContextMenu.Show(chrome, menu.Bounds.PointAt(Origin.BottomLeft), PlanMenu(chat, plan));

        int all = plan.Items.Count;
        var counts = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 20), Anchor = Origin.Left, Pivot = Origin.Left });
        counts.Add(new Label
        {
            Text = UIRichText.Literal(Said("noon.plan_progress", ("done", done.ToString(CultureInfo.InvariantCulture)), ("all", all.ToString(CultureInfo.InvariantCulture)))),
            TextScale = PLAN_TEXT,
            Dim = true,
            Align = Origin.Left
        });
        PlanBadge(counts, doing, "noon.plan_count_doing", UIBadgeTone.Info, chat?.Running == true);
        PlanBadge(counts, blocked, "noon.plan_count_blocked", UIBadgeTone.Warning, false);
        PlanBadge(counts, pending, "noon.plan_count_pending", UIBadgeTone.Neutral, false);
        PlanBadge(counts, dropped, "noon.plan_count_dropped", UIBadgeTone.Neutral, false);

        // Steps done are good news the whole way, green, not the context's green to amber to red
        // An empty track for none done, the bar's least width read as a step done
        if (all > 0)
        {
            Panel bar = card.Add(ContextBar((float)done / all, PlanWidth() - 24, skin?.Palette.Success ?? new Vector4(0.35f, 0.75f, 0.45f, 1.0f)));
            if (done == 0)
                foreach (UIComponent fill in bar.Children)
                    fill.Visible = false;
        }

        // What the person changed and the agent hasn't heard yet, and when it will
        if (chat is not null && planTold.ContainsKey(chat.Id))
            card.Add(ContextText(Said(chat.Running ? "noon.plan_told_running" : "noon.plan_told_next"), dim: true));
    }

    private static void PlanBadge(StackPanel into, int count, string key, UIBadgeTone tone, bool pulse)
    {
        if (count > 0)
            into.Add(new Badge { Text = Said(key, ("count", count.ToString(CultureInfo.InvariantCulture))), Tone = tone, Dot = pulse, Pulse = pulse, TextScale = 0.11f, Anchor = Origin.Left, Pivot = Origin.Left });
    }

    private StackPanel PlanCard(Vector4 outline) => new()
    {
        Fill = UIFill.Horizontal,
        Spacing = 6,
        Padding = new UIEdges(12, 10, 12, 10),
        Radius = 8,
        Color = (Skin?.Palette.Field ?? new Vector4(0.1f, 0.1f, 0.12f, 1.0f)) with { W = 0.6f },
        Outline = outline
    };

    // A plan the agent stopped on, steps still to do and none being done, waits for the person's go: asking for a plan tells
    // it to. Go says so in the chat, Change the plan puts words in the box to be finished
    private void PlanWaiting(HexAgentChat? shown, HexAgentPlan plan, int doing, int pending)
    {
        if (shown is not { Running: false } chat || pending == 0 || doing > 0)
            return;

        // Greyed out where nothing can be sent, an agent system not installed
        bool can = CanComposeAgent();
        var row = noonPlanRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8, Size = new Vector2(0, 30), Anchor = Origin.Left, Pivot = Origin.Left });
        row.Add(new Button
        {
            Label = Said("noon.plan_go"),
            Icon = "play",
            Primary = true,
            Enabled = can,
            Size = new Vector2(84, 28),
            LabelScale = 0.13f,
            Animated = false,
            OnPressed = () =>
            {
                if (agentChat != chat || chat.Running)
                    return;
                ShowStage(NoonStage.Chat);
                agentsInput.Text = Said("noon.plan_go_text");
                SendToAgent();
            }
        });
        string change = Said("noon.plan_change");
        row.Add(new Button
        {
            Label = change,
            Icon = "edit",
            Style = "button_flat",
            Enabled = can,
            Size = new Vector2(ButtonWidth(change, 0.13f), 28),
            LabelScale = 0.13f,
            Animated = false,
            OnPressed = () =>
            {
                ShowStage(NoonStage.Chat);
                agentsInput.Text = Said("noon.plan_change_text");
                agentsInput.Focus();
            }
        });
        row.Add(new Label { Text = UIRichText.Literal(Said("noon.plan_waiting")), TextScale = PLAN_TEXT, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
    }

    // The filters, a flat button each with its count, the one up marked
    private void PlanFilters(int open, int blocked, int done, int all)
    {
        var row = noonPlanRows.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4, Size = new Vector2(0, 28), Anchor = Origin.Left, Pivot = Origin.Left });
        void Filter(NoonPlanFilter filter, string key, int count)
        {
            string label = $"{Said(key)}  {count.ToString(CultureInfo.InvariantCulture)}";
            row.Add(new Button
            {
                Label = label,
                Style = "button_flat",
                Selected = noonPlanFilter == filter,
                Size = new Vector2(ButtonWidth(label, 0.13f) - 14, 26),
                LabelScale = 0.13f,
                Animated = false,
                Tooltip = Said(key + "_tip"),
                OnPressed = () =>
                {
                    noonPlanFilter = filter;
                    RedrawPlan();
                }
            });
        }

        Filter(NoonPlanFilter.Open, "noon.plan_filter_open", open);
        Filter(NoonPlanFilter.Blocked, "noon.plan_filter_blocked", blocked);
        Filter(NoonPlanFilter.Done, "noon.plan_filter_done", done);
        Filter(NoonPlanFilter.All, "noon.plan_filter_all", all);
    }

    // A section's name over its steps, with how many
    private void PlanSection(string title, int count) =>
        noonPlanRows.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 22), Padding = new UIEdges(4, 6, 0, 0), Color = Vector4.Zero })
            .With(new Label
            {
                Text = UIRichText.Literal($"{title}  {count.ToString(CultureInfo.InvariantCulture)}"),
                TextScale = 0.13f,
                Bold = true,
                Dim = true,
                Align = Origin.Left,
                Anchor = Origin.BottomLeft,
                Pivot = Origin.BottomLeft
            });

    private void PlanNothing(string text) =>
        noonPlanRows.Add(new Label { Text = UIRichText.Literal(text), TextScale = PLAN_TEXT, Dim = true, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left, Padding = new UIEdges(10, 0, 0, 0) });

    // A row that folds what's under it away, its chevron saying which way
    private void PlanFold(string label, string icon, bool shown, Action toggle)
    {
        noonPlanRows.Add(new Button
        {
            Label = $"$({(shown ? "chevron-down" : "chevron-right")}) $({icon}) {UIRichText.Literal(label)}",
            Style = "button_flat",
            Align = Origin.Left,
            Fill = UIFill.Horizontal,
            Size = new Vector2(0, 26),
            LabelScale = 0.13f,
            Animated = false,
            Padding = new UIEdges(6, 0, 6, 0),
            OnPressed = () =>
            {
                toggle();
                RedrawPlan();
            }
        });
    }

    // The earlier tasks, folded, each its title over its steps once opened, the latest first
    private void PlanEarlier(HexAgentPlan plan)
    {
        if (plan.Earlier.Count == 0)
            return;
        string count = plan.Earlier.Count.ToString(CultureInfo.InvariantCulture);
        PlanFold(Said(plan.Earlier.Count == 1 ? "noon.plan_earlier_one" : "noon.plan_earlier", ("count", count)), "history", noonPlanEarlierShown, () => noonPlanEarlierShown = !noonPlanEarlierShown);
        if (!noonPlanEarlierShown)
            return;

        string dim = HexColour(Skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
        string tick = HexColour(Skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f));
        for (int s = plan.Earlier.Count - 1; s >= 0; s--)
        {
            HexPlanSection section = plan.Earlier[s];
            var card = noonPlanRows.Add(PlanCard(Vector4.Zero));
            string title = section.Title is { Length: > 0 } named ? named : Said("noon.plan_earlier_untitled");
            string when = section.At > 0 ? $"  [color:{dim}]{UIRichText.Literal(HexGit.Ago(DateTimeOffset.FromUnixTimeMilliseconds(section.At)))}[/color]" : string.Empty;
            card.Add(new Label { Text = $"[b]{UIRichText.Literal(Fit(title, PlanWidth() - 160, PLAN_TEXT))}[/b]{when}", TextScale = PLAN_TEXT, Align = Origin.Left, Fill = UIFill.Horizontal });
            foreach (HexPlanItem item in section.Items)
            {
                string mark = item.State == HexPlanState.Done ? $"[color:{tick}]$(pass-filled)[/color]" : "$(close)";
                card.Add(new Label
                {
                    Text = $"{mark} [color:{dim}]{UIRichText.Literal(item.Text)}[/color]",
                    TextScale = PLAN_SMALL + 0.005f,
                    Wrap = true,
                    Fill = UIFill.Horizontal,
                    Align = Origin.Left
                });
            }
        }
    }

    // The box a step is added with, under the steps next, Enter or its button adding it
    private void PlanAddBox(HexAgentChat? chat)
    {
        if (chat is null)
            return;
        var row = noonPlanRows.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 30), Padding = new UIEdges(4, 2, 0, 2), Color = Vector4.Zero });
        noonPlanAdd ??= new TextBox { Fill = UIFill.Horizontal, Size = new Vector2(0, 26), MaxLength = 500, Placeholder = Said("noon.plan_add_hint") };
        noonPlanAdd.OnSubmitted = _ => AddPlanStep(chat);
        string label = Said("noon.plan_add");
        float width = ButtonWidth(label, 0.13f);
        var box = row.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, 26), Padding = new UIEdges(0, 0, width + 6, 0), Color = Vector4.Zero, Anchor = Origin.Left, Pivot = Origin.Left });
        // Off the row it was on before this draw, the rows were let go of but it was still theirs
        noonPlanAdd.Parent?.Remove(noonPlanAdd);
        box.Add(noonPlanAdd);
        row.Add(new Button
        {
            Label = label,
            Icon = "add",
            Style = "button_flat",
            Size = new Vector2(width, 26),
            LabelScale = 0.13f,
            Animated = false,
            Anchor = Origin.Right,
            Pivot = Origin.Right,
            OnPressed = () => AddPlanStep(chat)
        });
    }

    private void AddPlanStep(HexAgentChat chat)
    {
        if (noonPlanAdd is not { } box || box.Text.Trim() is not { Length: > 0 } text)
            return;
        string? id = null;
        if (EditPlan(chat, plan => plan.Added(text, out id), () => $"added {id} \"{OneLine(text, 120)}\" (pending)"))
        {
            box.Text = string.Empty;
            box.Focus();
        }
    }

    /// <summary>
    /// One step's row: its state's icon and words, who has it and when it was done, its note under it where it wants
    /// reading (going on or blocked), the row opening its details and its menu at the right.
    /// </summary>
    private void PlanRow(StackPanel into, HexAgentChat? chat, HexAgentPlan plan, HexPlanItem item, int? number)
    {
        UISkin? skin = Skin;
        string dim = HexColour(skin?.Palette.DimText ?? new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
        string warn = HexColour(skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f));
        bool opened = item.Id is not null && noonPlanOpened == item.Id;
        bool lead = item.State is HexPlanState.Doing or HexPlanState.Blocked;
        string? note = lead && !opened ? item.Note : null;
        float width = PlanWidth();

        // The step going on is a card of its own in the accent, the one thing the tab leads with
        StackPanel holder = into;
        if (item.State == HexPlanState.Doing)
        {
            holder = into.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 0, Padding = new UIEdges(2, 2, 2, 2), Radius = 8, Color = (skin?.AccentColor ?? Vector4.One) with { W = 0.08f }, Outline = (skin?.AccentColor ?? Vector4.One) with { W = 0.55f } });
            width -= 4;
        }

        var root = holder.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, note is null ? PLAN_ROW : PLAN_ROW_NOTE), Color = Vector4.Zero });
        var pick = root.Add(new Button { Style = "button_flat", Fill = UIFill.Both, Label = string.Empty, Animated = false, Selected = opened, Tooltip = Said(opened ? "noon.plan_row_close_tip" : "noon.plan_row_tip") });
        pick.OnPressed = () =>
        {
            noonPlanOpened = opened ? null : item.Id;
            RedrawPlan();
        };
        if (chat is not null)
            pick.OnContextMenu = at => ContextMenu.Show(chrome, at, PlanStepMenu(chat, item));

        // At the right: the menu, who has it, and when it was done
        float right = 8;
        if (chat is not null)
        {
            var menu = root.Add(new Button
            {
                Icon = "ellipsis",
                Style = "button_flat",
                Size = new Vector2(24, 22),
                Anchor = Origin.TopRight,
                Pivot = Origin.TopRight,
                Position = new Vector2(-4, -3),
                Animated = false,
                Tooltip = Said("noon.plan_step_menu_tip")
            });
            menu.OnPressed = () => ContextMenu.Show(chrome, menu.Bounds.PointAt(Origin.BottomLeft), PlanStepMenu(chat, item));
            right += 28;
        }

        if (item.Agent is { } owner)
        {
            string who = $"#{owner.ToString(CultureInfo.InvariantCulture)}";
            root.Add(new Badge { Text = who, Tone = UIBadgeTone.Neutral, TextScale = 0.105f, Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(-right, -6), Tooltip = Said("noon.plan_owner", ("name", ChatName(owner))) });
            right += 12 + (Skin?.Font.Measure(who, 0.105f, markup: false).X ?? 20) + 8;
        }

        if (item.State == HexPlanState.Done && item.DoneAt > 0)
        {
            string ago = HexGit.Ago(DateTimeOffset.FromUnixTimeMilliseconds(item.DoneAt));
            root.Add(new Label { Text = UIRichText.Literal(ago), TextScale = PLAN_SMALL, Dim = true, Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(-right, -6) });
            right += (Skin?.Font.Measure(ago, PLAN_SMALL, markup: false).X ?? 60) + 10;
        }

        string icon = item.State switch
        {
            HexPlanState.Done => $"[color:{HexColour(skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f))}]$(pass-filled)[/color]",
            HexPlanState.Doing => $"[color:{HexColour(AccentText(skin))}]$(loading~spin)[/color]",
            HexPlanState.Blocked => $"[color:{warn}]$(warning)[/color]",
            HexPlanState.Dropped => $"[color:{dim}]$(circle-slash)[/color]",
            _ => "$(circle-large-outline)"
        };
        string numbered = number is { } n ? $"[color:{dim}]{n.ToString(CultureInfo.InvariantCulture)}.[/color] " : string.Empty;
        float room = width - 40 - right - (number is null ? 0 : 18);
        string shown = Fit(item.Text, room, PLAN_TEXT);
        string fitted = UIRichText.Literal(shown);
        string words = item.State switch
        {
            HexPlanState.Doing => $"[b]{fitted}[/b]",
            HexPlanState.Done => $"[color:{dim}]{fitted}[/color]",
            HexPlanState.Dropped => $"[color:{dim}]{fitted} · {UIRichText.Literal(Said("noon.plan_dropped_word"))}[/color]",
            _ => fitted
        };
        root.Add(new Label { Text = $"{icon} {numbered}{words}", TextScale = PLAN_TEXT, Align = Origin.Left, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -6) });
        if (note is not null)
        {
            string colour = item.State == HexPlanState.Blocked ? warn : dim;
            root.Add(new Label
            {
                Text = $"[color:{colour}]{UIRichText.Literal(Fit(note, width - 40 - 36, PLAN_SMALL))}[/color]",
                TextScale = PLAN_SMALL,
                Align = Origin.Left,
                Anchor = Origin.TopLeft,
                Pivot = Origin.TopLeft,
                Position = new Vector2(32, -26)
            });
        }

        if (opened && chat is not null)
            PlanDetails(holder, chat, item, cut: shown != OneLine(item.Text, 400));
    }

    // A step's details under its row: all its words, its note, who has it, its files to open and what can be done with it,
    // the next move first and dropping it last in the error's colour
    private void PlanDetails(StackPanel into, HexAgentChat chat, HexPlanItem item, bool cut)
    {
        var card = into.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 6, Padding = new UIEdges(32, 2, 10, 10), Color = Vector4.Zero });
        // All its words only where the row had to cut them
        if (cut)
            card.Add(new Label { Text = UIRichText.Literal(item.Text), TextScale = PLAN_TEXT, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
        if (item.Note is { Length: > 0 } note)
        {
            string colour = HexColour(item.State == HexPlanState.Blocked ? Skin?.Palette.Warning ?? Vector4.One : Skin?.Palette.DimText ?? Vector4.One);
            card.Add(new Label { Text = $"[color:{colour}]$(note) {UIRichText.Literal(note)}[/color]", TextScale = PLAN_SMALL + 0.005f, Wrap = true, Fill = UIFill.Horizontal, Align = Origin.Left });
        }

        if (item.Agent is { } owner && ChatById(owner) is { } has && has != chat)
        {
            string label = Said("noon.plan_owner", ("name", ChatName(owner)));
            card.Add(new Button
            {
                Icon = "robot",
                Label = UIRichText.Literal(label),
                Style = "button_flat",
                Align = Origin.Left,
                Anchor = Origin.Left,
                Pivot = Origin.Left,
                Size = new Vector2(ButtonWidth(label, PLAN_SMALL), 22),
                LabelScale = PLAN_SMALL,
                Animated = false,
                OnPressed = () => OpenAgentChat(has)
            });
        }

        foreach (string file in item.Files ?? [])
        {
            string path = FullPathOf(file);
            card.Add(new Button
            {
                Icon = HexExplorer.FileIcon(path),
                IconColor = explorer.FileIconColor(path),
                Label = UIRichText.Literal(OneLine(file, 90)),
                Style = "button_flat",
                Align = Origin.Left,
                Anchor = Origin.Left,
                Pivot = Origin.Left,
                Size = new Vector2(MathF.Min(PlanWidth() - 60, ButtonWidth(file, PLAN_SMALL) + 10), 22),
                LabelScale = PLAN_SMALL,
                Animated = false,
                Tooltip = Said("noon.plan_file_tip"),
                OnPressed = () =>
                {
                    ShowFace(HexFace.Dawn);
                    Open(path);
                }
            });
        }

        var buttons = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6, Size = new Vector2(0, 28), Anchor = Origin.Left, Pivot = Origin.Left });
        void Act(string key, string icon, bool primary, bool danger, Action pressed)
        {
            string label = Said(key);
            buttons.Add(new Button
            {
                Label = label,
                Icon = icon,
                Primary = primary,
                Danger = danger,
                Style = primary ? string.Empty : "button_flat",
                Size = new Vector2(ButtonWidth(label, 0.13f), 26),
                LabelScale = 0.13f,
                Animated = false,
                OnPressed = pressed
            });
        }

        string id = item.Id!;
        switch (item.State)
        {
            case HexPlanState.Pending:
                Act("noon.plan_start", "play", true, false, () => SetStepState(chat, item, HexPlanState.Doing));
                Act("noon.plan_mark_done", "check", false, false, () => SetStepState(chat, item, HexPlanState.Done));
                break;
            case HexPlanState.Doing:
                Act("noon.plan_mark_done", "check", true, false, () => SetStepState(chat, item, HexPlanState.Done));
                break;
            case HexPlanState.Blocked:
                Act("noon.plan_unblock", "debug-continue", true, false, () => SetStepState(chat, item, HexPlanState.Pending));
                break;
            default:
                Act("noon.plan_reopen", "redo", true, false, () => SetStepState(chat, item, HexPlanState.Pending));
                break;
        }

        Act("noon.plan_edit", "edit", false, false, () => EditStepText(chat, item));
        if (item.State != HexPlanState.Blocked && item.Open)
            Act("noon.plan_block", "warning", false, false, () => BlockStep(chat, item));
        if (item.Open)
            Act("noon.plan_drop", "circle-slash", false, true, () => SetStepState(chat, item, HexPlanState.Dropped));
        else
            Act("noon.plan_delete", "trash", false, true, () => EditPlan(chat, plan => plan.Removed(id), () => $"deleted {id} \"{OneLine(item.Text, 80)}\""));
    }

    // What can be done with a step from its menu, its marks, its words, its place, and dropping or deleting it last
    private Menu PlanStepMenu(HexAgentChat chat, HexPlanItem item)
    {
        var menu = new Menu("plan step");
        string? id = item.Id;
        if (item.Open)
        {
            if (item.State != HexPlanState.Doing)
                menu.Add(Said("noon.plan_start"), () => SetStepState(chat, item, HexPlanState.Doing), icon: "play");
            menu.Add(Said("noon.plan_mark_done"), () => SetStepState(chat, item, HexPlanState.Done), icon: "check");
            if (item.State != HexPlanState.Pending)
                menu.Add(Said("noon.plan_mark_pending"), () => SetStepState(chat, item, HexPlanState.Pending), icon: "circle-large-outline");
            if (item.State != HexPlanState.Blocked)
                menu.Add(Said("noon.plan_block_dots"), () => BlockStep(chat, item), icon: "warning");
        }
        else
            menu.Add(Said("noon.plan_reopen"), () => SetStepState(chat, item, HexPlanState.Pending), icon: "redo");

        menu.AddSeparator();
        menu.Add(Said("noon.plan_edit_dots"), () => EditStepText(chat, item), icon: "edit");
        menu.Add(Said("noon.plan_note_dots"), () => EditStepNote(chat, item), icon: "note");
        if (item.Open && id is not null)
        {
            // Worked out once as the menu opens, not every frame it's up
            bool up = HexAgentPlan.Of(chat)?.Moved(id, -1) is not null, down = HexAgentPlan.Of(chat)?.Moved(id, 1) is not null;
            menu.Add(Said("noon.plan_move_up"), () => EditPlan(chat, plan => plan.Moved(id, -1), () => $"moved {id} up"), icon: "arrow-up")
                .IsEnabled = () => up;
            menu.Add(Said("noon.plan_move_down"), () => EditPlan(chat, plan => plan.Moved(id, 1), () => $"moved {id} down"), icon: "arrow-down")
                .IsEnabled = () => down;
        }

        menu.AddSeparator();
        if (item.Open)
            menu.Add(Said("noon.plan_drop"), () => SetStepState(chat, item, HexPlanState.Dropped), icon: "circle-slash", destructive: true);
        if (id is not null)
            menu.Add(Said("noon.plan_delete"), () => EditPlan(chat, plan => plan.Removed(id), () => $"deleted {id} \"{OneLine(item.Text, 80)}\""), icon: "trash", destructive: true);
        return menu;
    }

    // The plan's own menu: adding a step, copying it as a list, and putting the steps done aside last
    private Menu PlanMenu(HexAgentChat? chat, HexAgentPlan plan)
    {
        var menu = new Menu("plan");
        if (chat is not null)
            menu.Add(Said("noon.plan_add_step"), () =>
            {
                if (noonPlanFilter is NoonPlanFilter.Blocked or NoonPlanFilter.Done)
                {
                    noonPlanFilter = NoonPlanFilter.Open;
                    RedrawPlan();
                }
                noonPlanAdd?.Focus();
            }, icon: "add");
        menu.Add(Said("noon.plan_copy"), () =>
        {
            UICompositor.Copy(PlanAsList(plan));
            Say(Said("agents_panel.copied"));
        }, icon: "copy");
        if (chat is not null)
        {
            menu.AddSeparator();
            menu.Add(Said("noon.plan_clear_done"), () => EditPlan(chat, now => now.ClearDone(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), () => "put the done and dropped steps aside as an earlier task"),
                icon: "clear-all", destructive: true).IsEnabled = () => plan.Items.Any(item => item.State is HexPlanState.Done or HexPlanState.Dropped);
        }
        return menu;
    }

    // The plan as a Markdown checklist, for pasting
    private static string PlanAsList(HexAgentPlan plan)
    {
        var said = new StringBuilder();
        if (plan.Title is { Length: > 0 } title)
            said.Append("## ").Append(title).Append('\n');
        foreach (HexPlanItem item in plan.Items)
        {
            said.Append(item.State switch { HexPlanState.Done => "- [x] ", HexPlanState.Dropped => "- [-] ", HexPlanState.Doing => "- [~] ", _ => "- [ ] " }).Append(item.Text);
            if (item.State == HexPlanState.Blocked)
                said.Append(" (blocked").Append(item.Note is { Length: > 0 } why ? $": {why}" : string.Empty).Append(')');
            said.Append('\n');
        }
        return said.ToString();
    }

    private void SetStepState(HexAgentChat chat, HexPlanItem item, HexPlanState state)
    {
        if (item.Id is not { } id)
            return;
        EditPlan(chat, plan => plan.WithState(id, state), () => $"{id} \"{OneLine(item.Text, 80)}\" marked {state.ToString().ToLowerInvariant()}");
    }

    private void BlockStep(HexAgentChat chat, HexPlanItem item)
    {
        if (item.Id is not { } id)
            return;
        AskText(Said("noon.plan_block_title"), Said("noon.plan_block_message"), item.Note ?? string.Empty, Said("noon.plan_block_go"), why =>
            EditPlan(chat, plan => plan.WithState(id, HexPlanState.Blocked) is { } now ? now.WithNote(id, why) ?? now : null,
                () => $"{id} \"{OneLine(item.Text, 80)}\" marked blocked" + (string.IsNullOrWhiteSpace(why) ? string.Empty : $", because: {OneLine(why, 160)}")));
    }

    private void EditStepText(HexAgentChat chat, HexPlanItem item)
    {
        if (item.Id is not { } id)
            return;
        AskText(Said("noon.plan_edit_title"), Said("noon.plan_edit_message"), item.Text, Said("noon.plan_edit_go"), text =>
            EditPlan(chat, plan => plan.WithText(id, text), () => $"{id} now says \"{OneLine(text, 160)}\""), selected: (0, item.Text.Length));
    }

    private void EditStepNote(HexAgentChat chat, HexPlanItem item)
    {
        if (item.Id is not { } id)
            return;
        AskText(Said("noon.plan_note_title"), Said("noon.plan_note_message"), item.Note ?? string.Empty, Said("noon.plan_note_go"), note =>
            EditPlan(chat, plan => plan.WithNote(id, note), () => string.IsNullOrWhiteSpace(note) ? $"{id} note cleared" : $"{id} note: {OneLine(note, 160)}"));
    }

    /// <summary>
    /// The person's change to a chat's plan made, a plan from the agent system's notes given IDs first, and the agent to be
    /// told what it was. False when it changed nothing.
    /// </summary>
    private bool EditPlan(HexAgentChat chat, Func<HexAgentPlan, HexAgentPlan?> change, Func<string> told)
    {
        if (HexAgentPlan.Of(chat) is not { } plan)
            return false;
        if (plan.Items.Any(item => item.Id is null))
        {
            List<HexPlanItem> items = [.. plan.Items];
            for (int i = 0; i < items.Count; i++)
                if (items[i].Id is null)
                    items[i] = items[i] with { Id = HexAgentPlan.NewId(items) };
            plan = plan with { Items = items };
        }

        if (change(plan) is not { } changed)
            return false;
        chat.SetPlan(changed);
        TellPlan(chat, told());
        RedrawPlan();
        return true;
    }

    /* The agent told what the person changed */

    private void TellPlan(HexAgentChat chat, string what)
    {
        if (!planTold.TryGetValue(chat.Id, out List<string>? said))
            planTold[chat.Id] = said = [];
        said.Add(what);
        planToldAt = System.Environment.TickCount64;
    }

    private static string PlanToldText(List<string> said) =>
        $"The person changed your plan in the Plan tab: {string.Join("; ", said)}. Keep their changes, hex_plan_read shows the plan as it is now.";

    /// <summary>
    /// What the person changed of a working agent's plan given to it once they've stopped changing it for a moment, as a
    /// message it takes in as it works or finds in its inbox. An agent not working hears with the next message sent to it.
    /// </summary>
    private void TellPlansQuietly()
    {
        if (planTold.Count == 0 || System.Environment.TickCount64 - planToldAt < PLAN_TOLD_QUIET_MS)
            return;
        foreach (int id in planTold.Keys.ToArray())
        {
            if (ChatById(id) is not { } chat)
            {
                planTold.Remove(id);
                continue;
            }

            if (!chat.Running || !planTold.Remove(id, out List<string>? said))
                continue;
            Deliver(chat, null, "progress", PlanToldText(said), directive: false, fromName: FROM_PERSON);
            RedrawPlan();
        }
    }

    /// <summary>What the person changed of the chat's plan, to go in front of the next message sent to its agent, taken.</summary>
    private string? TakePlanTold(HexAgentChat? chat)
    {
        if (chat is null || !planTold.Remove(chat.Id, out List<string>? said))
            return null;
        return PlanToldText(said);
    }

    /* Measures */

    private float PlanWidth() => MathF.Max(320, noonPlan.Bounds.Width - 30);

    private float ButtonWidth(string label, float scale) =>
        MathF.Ceiling((Skin?.Font.Measure(label, scale, markup: false).X ?? label.Length * 7.0f) + 40);

    // Words cut to fit a width at a scale, an ellipsis where they were cut
    private string Fit(string text, float width, float scale)
    {
        string line = OneLine(text, 400);
        if (Skin?.Font is not { } font || font.Measure(line, scale, markup: false).X <= width)
            return line;
        int low = 0, high = line.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.Measure(string.Concat(line.AsSpan(0, mid), "…"), scale, markup: false).X <= width)
                low = mid;
            else
                high = mid - 1;
        }
        return string.Concat(line.AsSpan(0, low).TrimEnd(), "…");
    }
}
