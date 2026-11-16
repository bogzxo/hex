using System.Numerics;
using System.Text;

using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The chats of the Agents view as a list over the chat on screen, once there's more than one (or an agent started
// another). Each a row with how it's doing (working, done, failed, stopped, or a dot when it finished while it wasn't on
// screen), its name, and its agent, model and time quietly at the right. The agents an agent started are under it, in a
// step. A click opens a chat, the cross closes it, and its menu stops it, copies what it said or opens the one that
// started it. The head folds the list away and says how many are working.
internal sealed partial class DawnScene
{
    // A row's height, the most rows shown before the list scrolls, the head's height and how far in a step goes
    private const float CHAT_ROW = 26.0f, CHAT_ROWS_MOST = 6.0f, CHATS_HEAD = 22.0f, CHAT_STEP = 14.0f;

    private Panel agentsList = null!;
    private Button agentsListHead = null!;
    private ScrollPanel agentsListScroll = null!;
    private StackPanel agentsListRows = null!;
    private Button agentsActivity = null!;
    private bool agentsListOpen = true;
    private string agentsListDrawn = string.Empty;

    // How far down the chat starts under the head, the list's height and what the activity bar's robot says
    private float agentsHeadHeight = 58.0f;
    private float agentsListHeight;
    private int agentsWorkingShown = -1;

    private void BuildAgentsList()
    {
        agentsList = agentsView.Add(new Panel { Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal, Size = new Vector2(0, 0), Visible = false, Padding = new UIEdges(4, 0, 4, 0) });
        agentsListHead = agentsList.Add(new Button
        {
            Style = "button_flat", Align = Origin.Left, Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal,
            Size = new Vector2(0, CHATS_HEAD), LabelScale = 0.115f, Animated = false
        });
        agentsListHead.OnPressed = () =>
        {
            agentsListOpen = !agentsListOpen;
            agentsListDrawn = string.Empty;
        };
        agentsListScroll = agentsList.Add(new ScrollPanel { Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal, Position = new Vector2(0, -CHATS_HEAD), Fade = 8.0f });
        agentsListRows = agentsListScroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 1 });
        agentsActivity = layout.Get<Button>("act_agents");
    }

    /// <summary>The chats in the order the list shows them, the newest that somebody started first, each with those it started under it.</summary>
    private List<(HexAgentChat Chat, int Step)> ChatsInOrder()
    {
        var shown = new List<(HexAgentChat, int)>();
        var known = agentChats.Select(chat => chat.Id).ToHashSet();
        void Under(HexAgentChat chat, int step)
        {
            shown.Add((chat, step));
            foreach (HexAgentChat started in agentChats.Where(each => each.Parent == chat.Id))
                Under(started, step + 1);
        }

        // Those whose starter was closed are at the top with the rest
        for (int i = agentChats.Count - 1; i >= 0; i--)
        {
            if (agentChats[i].Parent is not { } parent || !known.Contains(parent))
                Under(agentChats[i], 0);
        }

        return shown;
    }

    /// <summary>
    /// The list drawn again when what it shows moved, the chats, which is open, how they're doing and the width. Shown
    /// once there's more than one chat or an agent started one.
    /// </summary>
    private void DrawAgentsList()
    {
        bool wanted = agentChats.Count > 1 || agentChats.Any(chat => chat.Parent is not null);
        float width = agentsView.Size.X;

        // Grows with the chats up to half the panel, and never less than the usual most. Only how many rows that is goes in
        // what it's drawn again for, and the width not at all, the rows follow it themselves (FitChatRows)
        float most = MathF.Max(CHAT_ROWS_MOST, MathF.Floor(agentsView.Size.Y * 0.5f / (CHAT_ROW + 1.0f)));
        var key = new StringBuilder().Append(wanted).Append(agentsListOpen).Append(most).Append(agentChat?.Id).Append('|');
        foreach (HexAgentChat chat in agentChats)
        {
            chat.Tick();
            key.Append((int)chat.TurnSeconds).Append(chat.Id).Append(chat.Version).Append(chat.Running).Append(chat.Unread).Append(chat.Stopped)
                .Append(chat.Shown).Append(';');
        }

        string said = key.ToString();
        if (said == agentsListDrawn)
        {
            FitChatRows(width);
            return;
        }
        agentsListDrawn = said;

        agentsList.Visible = wanted;
        float height = 0.0f;
        if (wanted)
        {
            List<(HexAgentChat Chat, int Step)> chats = ChatsInOrder();
            int working = agentChats.Count(chat => chat.Running);
            string head = $"$({(agentsListOpen ? "chevron-down" : "chevron-right")}) {Said("agents_panel.chats_head", ("count", agentChats.Count.ToString()))}";
            if (working > 0)
                head += $"  ·  $(loading~spin) {Said("agents_panel.chats_working", ("count", working.ToString()))}";
            agentsListHead.Label = head;

            Empty(agentsListRows);
            chatRowFits.Clear();
            chatRowsFitted = -1;
            if (agentsListOpen)
            {
                foreach (var (chat, step) in chats)
                    agentsListRows.Add(ChatRow(chat, step));
            }

            FitChatRows(width);
            float rows = agentsListOpen ? MathF.Min(chats.Count, most) : 0.0f;
            float listed = rows * (CHAT_ROW + 1.0f);
            agentsListScroll.Size = new Vector2(0, listed);
            agentsListScroll.Visible = agentsListOpen;
            height = CHATS_HEAD + listed + 6.0f;
        }

        agentsList.Size = new Vector2(0, height);
        agentsListHeight = height;
        PlaceAgentsList();
    }

    // What each chat's row does with a new width, and the width they were fitted to
    private readonly List<Action<float>> chatRowFits = [];
    private int chatRowsFitted = -1;

    // The chats' rows fitted to the width when it moved, their names cut to the room and their details shown or not
    private void FitChatRows(float width)
    {
        if ((int)width == chatRowsFitted)
            return;
        chatRowsFitted = (int)width;
        foreach (Action<float> fit in chatRowFits)
            fit(width);
    }

    /// <summary>The list under the head and the chat under the list.</summary>
    private void PlaceAgentsList()
    {
        agentsList.Position = new Vector2(0, -(agentsHeadHeight - 2.0f));
        agentsBody.Padding = new UIEdges(4, agentsHeadHeight + agentsListHeight, 4, 108);
    }

    // A chat's row, how it's doing and its name at the left, its agent, model and time at the right and a cross to close it
    private UIComponent ChatRow(HexAgentChat chat, int step)
    {
        string icon = chat.Running ? "$(loading~spin)"
            : chat.Unread ? "$(circle-filled)"
            : chat.Stopped ? "$(debug-stop)"
            : chat.Failed ? "$(error)"
            : chat.Parent is not null ? "$(pass)" : "$(comment)";

        string? model = chat.Options.GetValueOrDefault("model") is { Length: > 0 } picked ? picked : chat.Model?.Replace("claude-", string.Empty);
        string meta = string.Join(" · ", new[]
        {
            HexAgentProfiles.All.FirstOrDefault(profile => profile.Id == chat.Agent)?.Name,
            model,
            chat.TurnSeconds > 0 ? $"{chat.TurnSeconds:0}s" : null
        }.Where(part => !string.IsNullOrEmpty(part)));

        float indent = step * CHAT_STEP;
        string name = chat.Shown.Length > 0 ? chat.Shown : Said("agents_panel.new_chat");
        string at = OneLine(meta, 26);

        var row = new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, CHAT_ROW), Padding = new UIEdges(indent, 0, 0, 0) };
        var pick = row.Add(new Button
        {
            Style = "button_flat", Align = Origin.Left, Fill = UIFill.Both, LabelScale = 0.13f, Animated = false,
            Label = icon,
            Selected = chat == agentChat,
            Tooltip = $"#{chat.Id} {name}\n{meta}{(chat.Parent is { } parent ? "\n" + Said("agents_panel.started_by", ("name", ChatName(parent))) : string.Empty)}"
        });
        pick.OnPressed = () => OpenAgentChat(chat);
        pick.OnContextMenu = at => ContextMenu.Show(chrome, at, ChatMenu(chat));

        Label? details = meta.Length > 0
            ? row.Add(new Label { Text = UIRichText.Literal(at), TextScale = 0.105f, Dim = true, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-26, 0), Align = Origin.Right })
            : null;

        // As much of its name as there's room for beside what's at the right, and that only when there's room for it
        chatRowFits.Add(width =>
        {
            bool roomy = width - indent > 260;
            int room = Math.Max(6, (int)((width - indent - (roomy && details is not null ? at.Length * 6.3f + 70.0f : 60.0f)) / 6.6f) - 2);
            string label = $"{icon} {UIRichText.Literal(OneLine(name, room))}";
            if (pick.Label != label)
                pick.Label = label;
            if (details is not null)
                details.Visible = roomy;
        });

        var close = row.Add(new Button { Icon = "close", Style = "button_flat", Size = new Vector2(22, 22), Anchor = Origin.Right, Pivot = Origin.Right, Animated = false, Tooltip = Said("agents_panel.chat_close") });
        close.OnPressed = () => CloseAgentChat(chat);
        return row;
    }

    private string ChatName(int id) => ChatById(id) is { } chat ? $"#{id} {chat.Shown}" : $"#{id}";

    // What can be done to a chat from its row's menu
    private Menu ChatMenu(HexAgentChat chat)
    {
        var menu = new Menu("agent chat");
        menu.Add(Said("agents_panel.chat_open"), () => OpenAgentChat(chat), icon: "comment");
        if (chat.Parent is { } parent && ChatById(parent) is { } starter)
            menu.Add(Said("agents_panel.chat_open_parent", ("name", ChatName(parent))), () => OpenAgentChat(starter), icon: "arrow-up");
        menu.Add(Said("agents_panel.stop"), () => StopAgent(chat), icon: "debug-stop").IsEnabled = () => chat.Running;
        menu.Add(Said("agents_panel.chat_copy"), () =>
        {
            UICompositor.Copy(chat.LastReply(int.MaxValue));
            Say(Said("agents_panel.copied"));
        }, icon: "copy").IsEnabled = () => chat.LastReply().Length > 0;
        menu.AddSeparator();
        menu.Add(Said("agents_panel.chat_close"), () => CloseAgentChat(chat), icon: "close");
        return menu;
    }

    /// <summary>A chat put on screen, what was being written for the one before left behind.</summary>
    private void OpenAgentChat(HexAgentChat chat)
    {
        if (agentChat == chat)
            return;

        agentChat = chat;
        agentDraftContext = null;
        agentsInput.Text = string.Empty;
    }

    /// <summary>A chat closed, its agent stopped, and the agents it started closed with it.</summary>
    private void CloseAgentChat(HexAgentChat chat)
    {
        foreach (HexAgentChat started in agentChats.Where(each => each.Parent == chat.Id).ToList())
            CloseAgentChat(started);

        StopAgent(chat);
        agentChats.Remove(chat);
        if (agentChat == chat)
        {
            agentChat = chat.Parent is { } parent && ChatById(parent) is { } starter ? starter : agentChats.LastOrDefault();
            agentDraftContext = null;
            agentsInput.Text = string.Empty;
        }
    }

    /// <summary>The robot of the activity bar breathing while agents work, saying how many, so it shows with the view away.</summary>
    private void DrawAgentsActivity()
    {
        int working = agentChats.Count(chat => chat.Running);
        if (working == agentsWorkingShown || agentsActivity is null)
            return;

        agentsWorkingShown = working;
        agentsActivity.Icon = working > 0 ? "robot~breathe" : "robot";
        agentsActivity.Tooltip = working > 0 ? Said("agents_panel.working_tip", ("count", working.ToString())) : Said("activity.agents");
    }
}
