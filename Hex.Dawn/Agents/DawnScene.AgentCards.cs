using System.Globalization;
using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// An agent as a card, the same in the Agents view's list and in Noon's fleet. Along the top a badge of its agent system
// and who started it, with how it's doing at the right (working and for how long in blue, done, failed in red, stopped,
// or a dot when it finished unseen), its name under that, and under the name its model, what it cost, what's waiting for
// it or what it's doing now. The whole card is the button. A card is made once for what the list is and its words are
// put right in place after that, so a running agent's clock or its next tool never makes the card again under a click.
internal sealed partial class DawnScene
{
    // A card's height, room under its three lines for the bar of its context with a gap either side of it, the bar sat
    // against the line above it at seventy
    private const float AGENT_CARD = 82.0f;

    // Where a card's three lines start down from its top, an even padding above and below and the same gap between
    private const float CARD_TOP = 10.0f, CARD_TITLE = 29.0f, CARD_UNDER = 52.0f;

    // How far up from the card's foot its context bar sits
    private const float CONTEXT_BAR_FOOT = 9.0f;

    // The size of a card's small lines and of its name, a step up from what read too small at 100% scale
    private const float CARD_SMALL = 0.125f, CARD_NAME = 0.155f;

    // How thick the bar of a card's context is, thin enough to sit under its lines without being another line
    private const float CONTEXT_BAR = 3.0f;

    /// <summary>A card on screen and what puts its words right as its agent moves on, at the width it's at.</summary>
    private sealed record AgentCard(UIComponent Root, Action<float> Refresh);

    private readonly System.Text.StringBuilder agentCardBuilder = new(96);

    /// <summary>
    /// What a list of cards is made from as a number, the chats, their order and states, never their clocks, selection
    /// highlight or tools (which update in place via each card's Refresh callback).
    /// </summary>
    private long AgentCardsKey(List<(HexAgentChat Chat, int Step)> chats)
    {
        long key = 17;
        for (int i = 0; i < chats.Count; i++)
        {
            var (chat, step) = chats[i];
            key = key * 31 + chat.Id;
            key = key * 31 + step;
            key = key * 31 + string.GetHashCode(StateWord(chat), StringComparison.Ordinal);
            key = key * 31 + (chat.Unread ? 1 : 0);
            key = key * 31 + string.GetHashCode(chat.Shown, StringComparison.Ordinal);
            key = key * 31 + (chat.Parent ?? -1);
        }

        // And whether the cards carry the badge of the machine they run on (DawnScene.LinkFleet)
        return key * 31 + (LinkBadgesShown ? 1 : 0);
    }

    /// <param name="quiet">Its name dimmed, a finished chat's in a list where the working ones should read first.</param>
    /// <param name="rail">A rail of the warning colour down its left, a chat waiting on the person, the one thing in the list meant to catch the eye.</param>
    /// <param name="remote">The chat of another device the card is, its stand-in drawn and picking it opens that (DawnScene.LinkFleet).</param>
    private AgentCard AgentCardOf(HexAgentChat chat, int step, bool closable, bool quiet = false, bool rail = false, LinkRemoteCard? remote = null, HexAgentApprovalStatus? approval = null, bool waiting = false)
    {
        UISkin? skin = Skin;
        float indent = (remote is null ? step : Math.Min(step, 2)) * CHAT_STEP;
        var root = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, AGENT_CARD), Padding = new UIEdges(indent, 0, 0, 0), Color = Vector4.Zero };
        var pick = root.Add(new AgentCardButton(this, chat)
        {
            Style = "button_flat", Fill = UIFill.Both, Label = string.Empty, Animated = false, Card = root, Selected = remote is null ? chat == agentChat : linkRemoteOpen == remote,
            Tooltip = remote is not null && !LinkChatOnline(remote.DeviceId)
                ? $"#{chat.Id} {chat.Shown}\n{LinkChatOfflineReason(remote.DeviceName)}"
                : $"#{chat.Id} {chat.Shown}"
        });
        pick.OnPressed = () =>
        {
            if (remote is not null)
            {
                OpenRemoteChat(remote);
                return;
            }

            // Opened first, a narrow fleet giving the middle back, and the box given the keys only when it's the stage up
            OpenAgentChat(chat);
            if (Face == Hex.Shell.HexFace.Noon && noonStage == NoonStage.Chat)
                agentsInput.Focus();
        };
        pick.OnContextMenu = at => ContextMenu.Show(chrome, at, remote is null ? ChatMenu(chat) : LinkRemoteMenu(remote));

        // The badge, the agent system's letters on a tint of its colour
        HexAgentProfile? profile = FindAgentProfile(chat.Agent);
        string system = profile?.Name ?? chat.Agent;
        Vector4 tint = AgentTint(chat.Agent, skin);
        Vector4? remoteDim = remote is not null ? LinkChatDimInk() : null;
        if (remote is not null)
            tint = LinkChatReadableInk(tint, chip: true);
        // A pill of the system's letters in its own colour, the same badge as the card's state across from it
        var agentBadge = root.Add(new Badge { Text = Initials(system), Colour = tint, TextScale = 0.1f, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -CARD_TOP + 1), Tooltip = system });

        // The machine it runs on, while there's more than one to say (DawnScene.LinkFleet), and who started it after that
        float fromAt = 38 + LinkCardBadge(root, remote, -CARD_TOP + 1, out Badge? deviceBadge);
        var from = root.Add(new Label { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(fromAt, -CARD_TOP - 1), TextScale = CARD_SMALL, Dim = true, Color = remoteDim, Align = Origin.Left });
        // How it's doing as a badge in the colour of it, a beating dot while it works. It was words in a colour, which read
        // as one more line of the card's small print
        var state = root.Add(new Badge { Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(closable ? -30 : -10, -CARD_TOP + 2), TextScale = CARD_SMALL });
        var name = root.Add(new Label { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -CARD_TITLE), TextScale = CARD_NAME, Bold = chat == agentChat, Dim = quiet, Color = quiet ? remoteDim : null, Align = Origin.Left });
        if (rail)
        {
            Vector4 warning = skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f);
            root.Add(new Panel { Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(3, AGENT_CARD - 24), Position = new Vector2(2, 0), Radius = 1.5f, Color = warning with { W = 0.9f } });
        }
        var under = root.Add(new Label { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -CARD_UNDER), TextScale = CARD_SMALL, Dim = true, Color = remoteDim, Align = Origin.Left });
        // A double click on the name renames it in place, the box over it while it does (DawnScene.AgentsPanel.Rename)
        pick.Title = name;
        if (remote is not null)
            name.Tooltip = pick.Tooltip;

        // How full its context is, a thin bar along the card's foot in the colour of how full, there once the agent said
        var contextRow = root.Add(new Panel
        {
            Anchor = Origin.Bottom, Pivot = Origin.Bottom, Fill = UIFill.Horizontal, Size = new Vector2(0, CONTEXT_BAR), Position = new Vector2(0, CONTEXT_BAR_FOOT),
            Padding = new UIEdges(10, 0, 10, 0), Color = Vector4.Zero, Visible = false
        });
        var context = contextRow.Add(new HexShareBar { Fill = UIFill.Both, Track = (skin?.Palette.Track ?? new Vector4(0, 0, 0, 0.35f)) with { W = 0.5f } });

        if (closable)
        {
            var close = root.Add(new Button { Icon = "close", Style = "button_flat", Size = new Vector2(24, 24), Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(-3, -CARD_TOP + 5), Animated = false, Tooltip = Said("agents_panel.chat_close") });
            close.OnPressed = () => AskCloseAgentChat(chat);
        }

        // Over everything else on it, the box a name being renamed is typed in
        ChatCardMade(chat, root, pick, name);

        // What the card was last written from, its words made again only when one of these moved. Selection state is
        // updated in place so switching chats never rebuilds the fleet or sidebar card list.
        bool finishedQuiet = quiet || (chat.Settled && remote is null);
        (int Version, int Seconds, bool Unread, int Mail, int Width, string? Model, bool Stopped, bool Selected) drawn = (-1, -1, false, -1, -1, null, false, false);

        void Refresh(float width)
        {
            bool selected = remote is null ? chat == agentChat : linkRemoteOpen == remote;
            var now = (chat.Version, Seconds: chat.Running ? (int)chat.TurnSeconds : -1, chat.Unread, Mail: chat.UnreadMailCount(), Width: (int)width, chat.Model, chat.Stopped, Selected: selected);
            if (now == drawn)
                return;
            bool selectionOnly = drawn.Version == now.Version && drawn.Seconds == now.Seconds && drawn.Unread == now.Unread
                && drawn.Mail == now.Mail && drawn.Width == now.Width && drawn.Model == now.Model && drawn.Stopped == now.Stopped;
            drawn = now;
            pick.Selected = selected;
            name.Bold = selected;
            bool isQuiet = finishedQuiet && !selected;
            name.Dim = isQuiet;
            name.Color = isQuiet ? remoteDim : null;
            if (selectionOnly)
                return;
            float room = width - indent;
            FitChatRename(chat, room);
            string? model = HexAgentModels.Of(chat);
            // Who it's run by over its name, the agent system, and the chat that started it first for one an agent started.
            // The model goes with what it used under its name, where the model and what it cost read together
            // The row's own number leads (#12 · Claude Code), the chat that started it is on the tooltip
            string startedHere = remote is not null && linkStartedHere.Contains((remote.DeviceId, remote.Id))
                ? Said("link.chats_started_here") : string.Empty;
            string starter = remote is not null ? LinkStarterOf(remote, system, startedHere) : $"#{chat.Id} · {system}";
            string startedBy = remote is not null ? LinkStarterTip(remote, chat) : chat.Parent is { } startedParent ? Said("agents_panel.started_by", ("name", ChatName(startedParent))) : string.Empty;
            string starterTip = remote is null ? startedBy : startedBy.Length > 0 ? $"{starter}\n{startedBy}" : starter;
            if (from.Tooltip != starterTip)
                from.Tooltip = starterTip;
            ShowAgentStatus(state, chat, approval, waiting);

            // As much of it as fits beside the state, measured in the font, which a count of letters guessed at short
            float stateWidth = (Skin?.Font.Measure(state.Text, state.TextScale, markup: false).X ?? 100.0f) + 40.0f;
            string said;
            bool startedBelow = false;
            if (remote is not null && deviceBadge is not null)
            {
                state.Colour = skin is not null
                    ? LinkChatReadableInk(LinkBadgeColour(state.Tone) ?? state.ColourIn(skin), chip: true)
                    : null;
                state.Opacity = 1.0f;
                stateWidth = LinkChatChipWidth(state);
                float right = room - stateWidth - 16.0f;
                float deviceAt = 10.0f + LinkChatChipWidth(agentBadge) + 6.0f;
                string id = $"#{remote.Id} ·";
                float idWidth = (Skin?.Font.Measure(id, from.TextScale, markup: false).X ?? 48.0f) + 10.0f;
                float line = (Skin?.Font.LineHeight ?? 120) * deviceBadge.TextScale;
                float deviceRoom = MathF.Min(112.0f, right - deviceAt - idWidth - 6.0f);
                deviceBadge.Text = FitLinkChatText(OneLine(remote.DeviceName, 18), deviceBadge.TextScale, deviceRoom - 19 - line);
                deviceBadge.Visible = deviceBadge.Text.Length > 0;
                deviceBadge.Position = new Vector2(deviceAt, -CARD_TOP + 1);
                fromAt = deviceBadge.Visible ? deviceAt + LinkChatChipWidth(deviceBadge) + 6.0f : deviceAt;
                from.Position = new Vector2(fromAt, -CARD_TOP - 1);
                string fitted = FitLinkChatText(starter, from.TextScale, right - fromAt);
                startedBelow = startedHere.Length > 0 && fitted != starter;
                if (startedBelow)
                    fitted = FitLinkChatText(LinkStarterOf(remote, system, string.Empty), from.TextScale, right - fromAt);
                said = UIRichText.Literal(fitted);
            }
            else
                said = UIRichText.Literal(FitText(starter, from.TextScale, room - fromAt - stateWidth - (closable ? 30 : 10) - 10));
            if (from.Text != said)
                from.Text = said;

            said = UIRichText.Literal(FitText(chat.Shown.Length > 0 ? chat.Shown : Said("agents_panel.new_chat"), name.TextScale, room - 24, name.Bold));
            if (name.Text != said)
                name.Text = said;

            // What it's doing now while it works, its model, tokens, cost and what's waiting for it otherwise
            int unread = chat.UnreadMailCount();
            string? doing = chat.Running && chat.Entries.LastOrDefault(entry => entry.Kind == HexAgentEntryKind.Tool) is { State: HexAgentToolState.Running } tool
                ? (SubagentOf(tool) is { } subagent ? $"{subagent.Name} · {tool.Progress ?? subagent.About}" : $"{ToolTitle(tool.ToolName ?? "tool")} {ToolPreview(tool.Input)}")
                : null;
            // Cut to the card's width either way, a long model's name in front of the tokens ran past its edge. The mail's
            // icon is put on after the cut, cut into it would have left its name as text
            string mail = unread > 0 ? $" · $(mail) {unread}" : string.Empty;
            float mailWidth = mail.Length > 0 ? 40.0f : 0.0f;
            // The model stays in front of what it's doing, it was gone from the card for as long as a tool ran
            if (remote is not null)
            {
                string? extra = approval?.Summary ?? doing;
                if (extra is null && chat.Cost > 0)
                    extra = "$" + chat.Cost.ToString("0.000", CultureInfo.InvariantCulture);
                string details = JoinAgentCardDetails(model, extra);
                string? time = chat.TurnSeconds > 0 && (approval is not null || waiting || chat.RunGoing || chat.Failed || chat.Stopped)
                    ? Took(chat.TurnSeconds) : null;
                string tail = JoinAgentCardDetails(time, startedBelow ? startedHere : null);
                float tailWidth = tail.Length == 0 ? 0.0f : Skin?.Font.Measure(" · " + tail, under.TextScale, markup: false).X ?? 110.0f;
                string fitted = FitLinkChatText(details, under.TextScale, room - 24 - tailWidth);
                said = UIRichText.Literal(JoinAgentCardDetails(fitted, tail));
                string summary = JoinAgentCardDetails(details, tail);
                under.Tooltip = approval is { Detail.Length: > 0 } ? $"{summary}\n{approval.Detail}" : summary;
            }
            else if (approval is not null)
            {
                said = UIRichText.Literal(FitText(approval.Summary, under.TextScale, room - 24));
                under.Tooltip = approval.Detail.Length > 0 ? approval.Summary + "\n" + approval.Detail : approval.Summary;
            }
            else if (doing is not null)
                said = UIRichText.Literal(FitText(model is { Length: > 0 } ? $"{model} · {doing}" : doing, under.TextScale, room - 24));
            else
            {
                agentCardBuilder.Clear();
                if (!string.IsNullOrEmpty(model))
                    agentCardBuilder.Append(model);
                if (chat.ContextLimit > 0 && chat.ContextUsed > 0)
                {
                    if (agentCardBuilder.Length > 0) agentCardBuilder.Append(" · ");
                    agentCardBuilder.Append(Count(chat.ContextUsed)).Append(" / ").Append(Count(chat.ContextLimit));
                }
                else if (chat.InputTokens + chat.OutputTokens > 0)
                {
                    if (agentCardBuilder.Length > 0) agentCardBuilder.Append(" · ");
                    agentCardBuilder.Append(HexAgentLimits.Tokens(chat.InputTokens + chat.OutputTokens)).Append(" tokens");
                }
                if (chat.Cost > 0)
                {
                    if (agentCardBuilder.Length > 0) agentCardBuilder.Append(" · ");
                    agentCardBuilder.Append('$').Append(chat.Cost.ToString("0.000", CultureInfo.InvariantCulture));
                }
                string used = agentCardBuilder.ToString();
                said = UIRichText.Literal(FitText(used, under.TextScale, room - 24 - mailWidth)) + (used.Length > 0 ? mail : mail.TrimStart(' ', '·'));
            }
            if (under.Text != said)
                under.Text = said;

            bool known = chat.ContextLimit > 0 && chat.ContextUsed > 0;
            contextRow.Visible = known;
            if (known)
            {
                float share = Math.Clamp((float)chat.ContextUsed / chat.ContextLimit, 0.0f, 1.0f);
                context.Share = share;
                context.Colour = ContextColour(share);
                contextRow.Tooltip = Said("noon.context_of", ("used", Count(chat.ContextUsed)), ("limit", Count(chat.ContextLimit)), ("share", (share * 100).ToString("0", CultureInfo.InvariantCulture)));
            }
        }

        return new AgentCard(root, Refresh);
    }

    private static string JoinAgentCardDetails(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second ?? string.Empty : string.IsNullOrEmpty(second) ? first : $"{first} · {second}";

    private string FitLinkChatText(string text, float scale, float width)
    {
        if (width <= 0)
            return string.Empty;
        string fitted = FitText(text, scale, width);
        return Skin?.Font.Measure(fitted, scale, markup: false).X > width ? string.Empty : fitted;
    }

    /// <summary>
    /// A line cut with an ellipsis to the most of it that fits in <paramref name="width"/> pixels at a scale, measured in the
    /// skin's font, a bold line measured a bold letter's width wider so a bold cut doesn't still run past the edge.
    /// Whole when it fits or there's no font.
    /// </summary>
    private string FitText(string text, float scale, float width, bool bold = false)
    {
        UITextStyle style = bold ? UITextStyle.Bold : UITextStyle.None;
        if (Skin?.Font is not { } font || font.MeasureLine(text, scale, style, default, markup: false) <= width)
            return text;
        int low = 1, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (font.MeasureLine(OneLine(text, middle), scale, style, default, markup: false) <= width)
                low = middle;
            else
                high = middle - 1;
        }

        return OneLine(text, Math.Max(4, low));
    }

    // How an agent is doing in a word or two and its time, on its badge in the tone of it: working in blue with a beating
    // dot, failed in red, done and unseen in the accent with a dot, stopped, done and new quiet. In the texts, not English here
    private static void ShowAgentStatus(Badge badge, HexAgentChat chat, HexAgentApprovalStatus? approval, bool waiting)
    {
        string time = Took(chat.TurnSeconds);
        (string text, UIBadgeTone tone, string icon, bool dot) = approval is not null
            ? (Said("agents_panel.waiting_approval_short"), UIBadgeTone.Warning, "shield", false)
            : waiting ? (Said("link.chats_waiting_for_you"), UIBadgeTone.Warning, "watch", false)
            : chat.Running ? (Said("agents_panel.card_working", ("time", time)), UIBadgeTone.Info, "", true)
            // Its turn over but its program still going, on a build or a wait of its own: not done, and said so
            : chat.RunGoing ? (Said("agents_panel.card_waiting"), UIBadgeTone.Warning, "watch", false)
            : chat.Failed ? (Said("agents_panel.card_failed"), UIBadgeTone.Danger, "error", false)
            : chat.Stopped ? (Said("agents_panel.card_stopped"), UIBadgeTone.Neutral, "debug-stop", false)
            : chat.Unread ? (Said("agents_panel.card_done", ("time", time)), UIBadgeTone.Accent, "", true)
            : (chat.Entries.Count == 0 ? Said("agents_panel.card_new") : Said("agents_panel.card_done", ("time", time)), UIBadgeTone.Neutral, "", false);
        badge.Text = text;
        badge.Tone = tone;
        badge.Icon = icon;
        badge.Dot = dot;
        badge.Pulse = chat.Running && approval is null && !waiting;
        badge.Opacity = tone == UIBadgeTone.Neutral && !chat.Stopped ? 0.7f : 1.0f;
    }

    /// <summary>Seconds said short, 12s, 11m, 1h 5m, the same whatever the machine's language writes numbers in.</summary>
    internal static string Took(double seconds) => HexAgentRows.FormatDurationShort(seconds);

    /// <summary>An agent system's letters for its badge, CC for Claude Code, CO for Codex.</summary>
    internal static string Initials(string name)
    {
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string letters = words.Length > 1 ? string.Concat(words.Take(2).Select(word => word[0])) : name.Length > 1 ? name[..2] : name;
        return letters.ToUpperInvariant();
    }

    /// <summary>
    /// The blue of something going on, not a theme's selection colour, which is its accent in some. A deeper one on a light
    /// look, where the bright one was under 3:1 on white.
    /// </summary>
    internal static Vector4 RunningBlue(UISkin? skin) => skin?.Palette.LightField == true ? new Vector4(0.169f, 0.384f, 0.784f, 1.0f) : new Vector4(0.38f, 0.62f, 1.0f, 1.0f);

    /// <summary>
    /// The look's accent as words or an icon are drawn in it: the accent itself on a light look, lifted towards white on a
    /// dark one, where Noon's teal was under 4.5:1 on the ground. The accent itself stays the colour of a fill.
    /// </summary>
    internal static Vector4 AccentText(UISkin? skin)
    {
        Vector4 accent = skin?.Palette.Accent ?? new Vector4(0.29f, 0.69f, 0.71f, 1.0f);
        return skin?.Palette.LightField == true ? accent with { W = 1 } : Vector4.Lerp(accent with { W = 1 }, Vector4.One, 0.35f);
    }

    // The running blue of the look on now
    private Vector4 RUNNING_BLUE => RunningBlue(compositor.Skin);

    // The colour of an agent system's badge, its own as its extension says it (hex.agent's colour, colour_light on a light
    // theme), so it reads as that system wherever it's shown. One that says none has one of the theme's, the same every
    // time for the same system
    private static Vector4 AgentTint(string agent, UISkin? skin)
    {
        IReadOnlyList<HexAgentProfile> all = HexAgentProfiles.All;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Id != agent)
                continue;
            string? said = skin?.Palette.LightField == true ? all[i].ColourOnLight ?? all[i].Colour : all[i].Colour;
            if (said is { Length: > 0 } && ColorPicker.FromHex(said) is { } own)
                return own with { W = 1.0f };
            break;
        }

        Vector4[] tints =
        [
            skin?.Palette.Accent ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f),
            (skin?.Palette.Selection ?? new Vector4(0.38f, 0.62f, 1.0f, 1.0f)) with { W = 1.0f },
            skin?.Palette.Success ?? new Vector4(0.36f, 0.85f, 0.45f, 1.0f),
            skin?.Palette.Warning ?? new Vector4(1.0f, 0.78f, 0.3f, 1.0f)
        ];
        int hash = 0;
        foreach (char c in agent)
            hash = hash * 31 + c;
        return tints[Math.Abs(hash) % tints.Length];
    }
}
