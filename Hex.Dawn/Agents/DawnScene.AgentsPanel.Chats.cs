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
    private const float CHAT_ROWS_MOST = 4.0f, CHATS_HEAD = 22.0f, CHAT_STEP = 14.0f;

    private Panel agentsList = null!;
    private Button agentsListHead = null!;
    private ScrollPanel agentsListScroll = null!;
    private StackPanel agentsListRows = null!;
    private Button agentsActivity = null!;
    private bool agentsListOpen = true;
    private string agentsListDrawn = string.Empty;
    private Panel agentsSystemsHolder = null!;

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
        // The agent systems under the chats, folded to a line each until opened, the side bar's room being the chat's
        agentsSystemsHolder = agentsList.Add(new Panel { Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal, Color = Vector4.Zero });
        agentsSystems = MakeSystemsPanel(agentsSystemsHolder, open: false);
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
        // Noon has every chat down its left already, the list over the chat would say it twice
        bool wanted = Face != HexFace.Noon && (agentChats.Count > 1 || agentChats.Any(chat => chat.Parent is not null));
        float width = agentsView.Size.X;

        // The systems under the chats in Dawn, Noon has them under its fleet. Their words put right every time, the list
        // laid out again only when the room they take changed
        bool systems = Face != HexFace.Noon && agentsSystems is not null && AgentSystems().Length > 0;
        float systemsTall = 0.0f;
        if (systems)
        {
            DrawSystems(agentsSystems!, width - 8.0f);
            systemsTall = agentsSystems!.Root.Bounds.IsEmpty ? 0.0f : MathF.Ceiling(agentsSystems.Root.Bounds.Height);
        }

        // Grows with the chats up to half the panel, and never less than the usual most. Only how many rows that is goes in
        // what it's drawn again for, and the width not at all, the cards follow it themselves (UpdateAgentsPanel)
        // The cards are made again only when the chats or how they're doing changed. Their clocks and what they're on are
        // put right in place, a card made again every second under a click took the click with it
        float most = MathF.Max(CHAT_ROWS_MOST, MathF.Floor(agentsView.Size.Y * 0.5f / (AGENT_CARD + 1.0f)));
        foreach (HexAgentChat chat in agentChats)
            chat.Tick();
        int working = agentChats.Count(chat => chat.Running);
        string said = $"{wanted}{agentsListOpen}{most}{working}{systems}{systemsTall}|{AgentCardsKey(ChatsInOrder())}";
        if (said == agentsListDrawn)
        {
            foreach (Action<float> refresh in chatRowFits)
                refresh(width);
            return;
        }
        agentsListDrawn = said;

        agentsList.Visible = wanted || systems;
        agentsListHead.Visible = wanted;
        agentsListScroll.Visible = wanted && agentsListOpen;
        float height = 0.0f;
        if (wanted)
        {
            List<(HexAgentChat Chat, int Step)> chats = ChatsInOrder();
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
                {
                    AgentCard card = AgentCardOf(chat, step, closable: true);
                    agentsListRows.Add(card.Root);
                    chatRowFits.Add(card.Refresh);
                }
            }

            foreach (Action<float> refresh in chatRowFits)
                refresh(width);
            float rows = agentsListOpen ? MathF.Min(chats.Count, most) : 0.0f;
            float listed = rows * (AGENT_CARD + 1.0f);
            agentsListScroll.Size = new Vector2(0, listed);
            agentsListScroll.Visible = agentsListOpen;
            height = CHATS_HEAD + listed + 6.0f;
        }

        agentsSystemsHolder.Visible = systems;
        if (systems)
        {
            agentsSystemsHolder.Position = new Vector2(0, -height);
            agentsSystemsHolder.Size = new Vector2(0, systemsTall);
            height += systemsTall + 4.0f;
        }

        agentsList.Size = new Vector2(0, height);
        agentsListHeight = height;
        PlaceAgentsList();
    }

    // What puts each card's words right at a width, and the width they were last put right at
    private readonly List<Action<float>> chatRowFits = [];
    private int chatRowsFitted = -1;

    /// <summary>The list under the head and the chat under the list.</summary>
    private void PlaceAgentsList()
    {
        agentsList.Position = new Vector2(0, -(agentsHeadHeight - 2.0f));
        agentsBody.Padding = new UIEdges(4, agentsHeadHeight + agentsListHeight, 4, agentsComposeTall + 36.0f);
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
