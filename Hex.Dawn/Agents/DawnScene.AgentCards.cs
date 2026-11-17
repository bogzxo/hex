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
    // A card's height
    private const float AGENT_CARD = 66.0f;

    // Where a card's three lines start down from its top, an even padding above and below and the same gap between
    private const float CARD_TOP = 9.0f, CARD_TITLE = 26.0f, CARD_UNDER = 46.0f;

    /// <summary>A card on screen and what puts its words right as its agent moves on, at the width it's at.</summary>
    private sealed record AgentCard(UIComponent Root, Action<float> Refresh);

    /// <summary>What a list of cards is made from, the chats, their order and states, never their clocks or their tools.</summary>
    private string AgentCardsKey(IEnumerable<(HexAgentChat Chat, int Step)> chats) =>
        string.Join(';', chats.Select(each =>
            $"{each.Chat.Id}.{each.Step}.{StateWord(each.Chat)}.{each.Chat.Unread}.{each.Chat == agentChat}.{each.Chat.Shown}.{each.Chat.Parent}"));

    private AgentCard AgentCardOf(HexAgentChat chat, int step, bool closable)
    {
        UISkin? skin = Skin;
        float indent = step * CHAT_STEP;
        var root = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, AGENT_CARD), Padding = new UIEdges(indent, 0, 0, 0), Color = Vector4.Zero };
        var pick = root.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Label = string.Empty, Animated = false, Selected = chat == agentChat,
            Tooltip = $"#{chat.Id} {chat.Shown}"
        });
        pick.OnPressed = () =>
        {
            OpenAgentChat(chat);
            if (Face == Hex.Shell.HexFace.Noon)
                agentsInput.Focus();
        };
        pick.OnContextMenu = at => ContextMenu.Show(chrome, at, ChatMenu(chat));

        // The badge, the agent system's letters on a tint of its colour
        HexAgentProfile? profile = HexAgentProfiles.All.FirstOrDefault(each => each.Id == chat.Agent);
        string system = profile?.Name ?? chat.Agent;
        Vector4 tint = AgentTint(chat.Agent, skin);
        var badge = root.Add(new Panel { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -CARD_TOP), Size = new Vector2(22, 14), Radius = 3, Color = tint with { W = 0.22f } });
        badge.Add(new Label { Text = $"[color:{HexColour(tint)}]{Initials(system)}[/color]", TextScale = 0.085f, Bold = true, Anchor = Origin.Center, Pivot = Origin.Center });

        var from = root.Add(new Label { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(38, -CARD_TOP - 1), TextScale = 0.11f, Dim = true, Align = Origin.Left });
        var state = root.Add(new Label { Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(closable ? -30 : -10, -CARD_TOP - 1), TextScale = 0.11f, Align = Origin.Right });
        var name = root.Add(new Label { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -CARD_TITLE), TextScale = 0.145f, Bold = chat == agentChat, Align = Origin.Left });
        var under = root.Add(new Label { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Position = new Vector2(10, -CARD_UNDER), TextScale = 0.11f, Dim = true, Align = Origin.Left });

        if (closable)
        {
            var close = root.Add(new Button { Icon = "close", Style = "button_flat", Size = new Vector2(20, 20), Anchor = Origin.TopRight, Pivot = Origin.TopRight, Position = new Vector2(-4, -CARD_TOP + 3), Animated = false, Tooltip = Said("agents_panel.chat_close") });
            close.OnPressed = () => CloseAgentChat(chat);
        }

        void Refresh(float width)
        {
            float room = width - indent;
            string? model = HexAgentModels.Of(chat);
            // Who it is and the model it's on, always in sight, a working agent's line under its name being what it's doing
            string starter = (chat.Parent is { } parent ? ChatName(parent) : system) + (model is { Length: > 0 } ? $" · {model}" : string.Empty);
            string status = AgentStatusText(chat, skin);
            if (state.Text != status)
                state.Text = status;

            // As much of it as fits beside the state, measured in the font, which a count of letters guessed at short
            // Measured without its colour tags, which the font would count as letters
            string bare = System.Text.RegularExpressions.Regex.Replace(status, @"\[/?color[^\]]*\]", string.Empty);
            float stateWidth = Skin?.Font.Measure(bare, state.TextScale).X ?? 100.0f;
            string said = UIRichText.Literal(FitText(starter, from.TextScale, room - 38 - stateWidth - (closable ? 30 : 10) - 10));
            if (from.Text != said)
                from.Text = said;

            said = UIRichText.Literal(FitText(chat.Shown.Length > 0 ? chat.Shown : Said("agents_panel.new_chat"), name.TextScale, room - 24));
            if (name.Text != said)
                name.Text = said;

            // What it's doing now while it works, its model, cost and what's waiting for it otherwise
            int unread = chat.UnreadMail().Length;
            string? doing = chat.Running && chat.Entries.LastOrDefault(entry => entry.Kind == HexAgentEntryKind.Tool) is { State: HexAgentToolState.Running } tool
                ? (SubagentOf(tool) is { } subagent ? $"{subagent.Name} · {tool.Progress ?? subagent.About}" : $"{ToolTitle(tool.ToolName ?? "tool")} {ToolPreview(tool.Input)}")
                : null;
            string line = doing ?? string.Join(" · ", new[]
            {
                chat.InputTokens + chat.OutputTokens > 0 ? $"{HexAgentLimits.Tokens(chat.InputTokens + chat.OutputTokens)} tokens" : null,
                chat.Cost > 0 ? "$" + chat.Cost.ToString("0.000", CultureInfo.InvariantCulture) : null,
                unread > 0 ? $"$(mail) {unread}" : null
            }.Where(part => !string.IsNullOrEmpty(part)));
            said = doing is null ? line : UIRichText.Literal(FitText(line, under.TextScale, room - 24));
            if (under.Text != said)
                under.Text = said;
        }

        return new AgentCard(root, Refresh);
    }

    /// <summary>
    /// A line cut with an ellipsis to the most of it that fits in <paramref name="width"/> pixels at a scale, measured in the
    /// skin's font. Whole when it fits or there's no font.
    /// </summary>
    private string FitText(string text, float scale, float width)
    {
        if (Skin?.Font is not { } font || font.Measure(text, scale, markup: false).X <= width)
            return text;
        int low = 1, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (font.Measure(OneLine(text, middle), scale, markup: false).X <= width)
                low = middle;
            else
                high = middle - 1;
        }

        return OneLine(text, Math.Max(4, low));
    }

    // How an agent is doing in a word or two and its time, in the colour of it
    private static string AgentStatusText(HexAgentChat chat, UISkin? skin)
    {
        string time = Took(chat.TurnSeconds);
        if (chat.Running)
            return $"[color:{HexColour(RUNNING_BLUE)}]$(loading~spin) Working {time}[/color]";
        if (chat.Failed)
            return $"[color:{HexColour(skin?.Palette.Error ?? new Vector4(1.0f, 0.42f, 0.38f, 1.0f))}]$(error) Failed[/color]";
        if (chat.Stopped)
            return "$(debug-stop) Stopped";
        if (chat.Unread)
            return $"[color:{HexColour(skin?.Palette.Accent ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f))}]$(circle-filled) Done {time}[/color]";
        return chat.Entries.Count == 0 ? "New" : $"Done {time}";
    }

    /// <summary>Seconds said short, 12s, 11m, 1h 5m.</summary>
    internal static string Took(double seconds) =>
        seconds < 60 ? $"{seconds:0}s" : seconds < 3600 ? $"{seconds / 60:0}m" : $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m";

    /// <summary>An agent system's letters for its badge, CC for Claude Code, CO for Codex.</summary>
    internal static string Initials(string name)
    {
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string letters = words.Length > 1 ? string.Concat(words.Take(2).Select(word => word[0])) : name.Length > 1 ? name[..2] : name;
        return letters.ToUpperInvariant();
    }

    /// <summary>The blue of something going on, the same in every look, a theme's selection colour is its accent in some.</summary>
    internal static readonly Vector4 RUNNING_BLUE = new(0.38f, 0.62f, 1.0f, 1.0f);

    // The colour of an agent system's badge, the same one every time for the same system, Claude Code's the accent and
    // Codex's blue
    private static Vector4 AgentTint(string agent, UISkin? skin)
    {
        if (agent == "claude_code")
            return skin?.Palette.Accent ?? new Vector4(0.85f, 0.42f, 0.3f, 1.0f);
        if (agent == "codex")
            return RUNNING_BLUE;
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
