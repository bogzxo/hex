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
    // What the list was last drawn from as a number, NOT_DRAWN for drawing it again on the next tick
    private long agentsListDrawn = NOT_DRAWN;
    private const long NOT_DRAWN = long.MinValue;
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
            Size = new Vector2(0, CHATS_HEAD), LabelScale = 0.125f, Animated = false
        });
        agentsListHead.OnPressed = () =>
        {
            agentsListOpen = !agentsListOpen;
            agentsListDrawn = NOT_DRAWN;
        };
        agentsListScroll = agentsList.Add(new ScrollPanel { Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal, Position = new Vector2(0, -CHATS_HEAD), Fade = 8.0f });
        agentsListRows = agentsListScroll.Add(new StackPanel { Fill = UIFill.Horizontal, Spacing = 1 });
        // The agent systems under the chats, folded to a line each until opened, the side bar's room being the chat's
        agentsSystemsHolder = agentsList.Add(new Panel { Anchor = Origin.Top, Pivot = Origin.Top, Fill = UIFill.Horizontal, Color = Vector4.Zero });
        agentsSystems = MakeSystemsPanel(agentsSystemsHolder, open: false);
        agentsActivity = layout.Get<Button>("act_agents");
    }

    /// <summary>The chats in the order the list shows them, the newest that somebody started first, each with those it started under it.</summary>
    /// <remarks>
    /// Made again only when the chats or who started whom changed, the same list handed back otherwise, so it's read and
    /// never changed by whoever asks. It was made, with a set of the ids, every time the list and the fleet looked.
    /// </remarks>
    private List<(HexAgentChat Chat, int Step)> ChatsInOrder()
    {
        long key = 17;
        for (int i = 0; i < agentChats.Count; i++)
            key = (key * 31 + agentChats[i].Id) * 31 + (agentChats[i].Parent ?? -1);
        if (key == chatsOrderedFor && chatsOrdered.Count > 0 == agentChats.Count > 0)
            return chatsOrdered;

        chatsOrderedFor = key;
        var shown = new List<(HexAgentChat, int)>(agentChats.Count);
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

        chatsOrdered = shown;
        return shown;
    }

    // The chats in order as they were last worked out, and what of the chats they were worked out from
    private List<(HexAgentChat Chat, int Step)> chatsOrdered = [];
    private long chatsOrderedFor;

    /// <summary>
    /// The list drawn again when what it shows moved, the chats, which is open, how they're doing and the width. Shown
    /// once there's more than one chat or an agent started one.
    /// </summary>
    private void DrawAgentsList()
    {
        // Noon has every chat down its left already, the list over the chat would say it twice
        // Loops over the chats rather than LINQ, this runs ten times a second whether anything moved or not
        bool started = false;
        int working = 0;
        for (int i = 0; i < agentChats.Count; i++)
        {
            HexAgentChat chat = agentChats[i];
            chat.Tick();
            started |= chat.Parent is not null;
            working += chat.Running ? 1 : 0;
        }
        bool wanted = Face != HexFace.Noon && (agentChats.Count > 1 || started);
        float width = agentsView.Size.X;

        // The systems under the chats in Dawn, Noon has them under its fleet. Their words put right every time, the list
        // laid out again only when the room they take changed
        bool systems = Face != HexFace.Noon && agentsSystems is not null && AgentSystems().Length > 0;
        float systemsTall = 0.0f;
        if (systems)
        {
            DrawSystems(agentsSystems!, width - 8.0f);
            systemsTall = SystemsTall(agentsSystems);
        }

        // Grows with the chats up to half the panel, and never less than the usual most. Only how many rows that is goes in
        // what it's drawn again for, and the width not at all, the cards follow it themselves (UpdateAgentsPanel)
        // The cards are made again only when the chats or how they're doing changed. Their clocks and what they're on are
        // put right in place, a card made again every second under a click took the click with it
        float most = MathF.Max(CHAT_ROWS_MOST, MathF.Floor(agentsView.Size.Y * 0.5f / (AGENT_CARD + 1.0f)));
        // Not the systems' height, which eases a step every frame after a fold, the list only placed again for that
        long said = ((((AgentCardsKey(ChatsInOrder()) * 31 + (wanted ? 1 : 0)) * 31 + (agentsListOpen ? 1 : 0)) * 31 + (long)most) * 31 + working) * 31 + (systems ? 1 : 0);
        if (said == agentsListDrawn)
        {
            foreach (Action<float> refresh in chatRowFits)
                refresh(width);
            PlaceAgentsSystems(systems, systemsTall);
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

        agentsCardsHeight = height;
        agentsSystemsPlaced = -1.0f;
        PlaceAgentsSystems(systems, systemsTall);
    }

    // How tall the chats' part of the list is, and the systems' height it was last placed with
    private float agentsCardsHeight, agentsSystemsPlaced = -1.0f;

    /// <summary>The systems put under the chats and the list sized to both, only when the systems' height moved.</summary>
    private void PlaceAgentsSystems(bool systems, float systemsTall)
    {
        float tall = systems ? systemsTall : 0.0f;
        if (MathF.Abs(tall - agentsSystemsPlaced) < 0.5f && agentsSystemsHolder.Visible == systems)
            return;
        agentsSystemsPlaced = tall;
        float height = agentsCardsHeight;
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
        // The card sits on the bottom, the harness's line that was under it being in the head now
        agentsBody.Padding = new UIEdges(4, agentsHeadHeight + agentsListHeight, 4, agentsComposeTall + 12.0f + AgentsWaitStripTall);
        if (agentsWaitStrip is not null)
            agentsWaitStrip.Position = new Vector2(0, agentsComposeTall + 6.0f);
    }

    private string ChatName(int id) => ChatById(id) is { } chat ? $"#{id} {chat.Shown}" : $"#{id}";

    // What can be done to a chat from its row's menu
    private Menu ChatMenu(HexAgentChat chat)
    {
        var menu = new Menu("agent chat");
        menu.Add(Said("agents_panel.chat_open"), () => OpenAgentChat(chat), icon: "comment");
        // The same chat in the other place it's shown, Noon's middle or the side bar
        if (Face == HexFace.Noon)
            menu.Add(Said("agents_panel.chat_open_side"), () => { ShowFace(HexFace.Dawn); ShowAgentsPanel(); OpenAgentChat(chat); }, icon: "layout-sidebar-left");
        else
            menu.Add(Said("agents_panel.chat_open_noon"), () => { ShowFace(HexFace.Noon); OpenAgentChat(chat); }, icon: "robot");
        if (chat.Parent is { } parent && ChatById(parent) is { } starter)
            menu.Add(Said("agents_panel.chat_open_parent"), () => OpenAgentChat(starter), icon: "arrow-up");
        menu.AddSeparator();
        menu.Add(Said("agents_panel.chat_rename"), () => RenameAgentChat(chat), icon: "edit");
        menu.Add(Said("agents_panel.chat_copy"), () =>
        {
            UICompositor.Copy(chat.LastReply(int.MaxValue));
            Say(Said("agents_panel.copied"));
        }, icon: "copy").IsEnabled = () => chat.LastReply().Length > 0;
        menu.Add(Said("agents_panel.chat_stop"), () => StopAgent(chat), icon: "debug-stop").IsEnabled = () => chat.Running;
        if (HasRunningStarted(chat))
            menu.Add(Said("agents_panel.chat_stop_all"), () => StopAgent(chat, withStarted: true), icon: "debug-stop");
        menu.AddSeparator();
        // Said with its subagents only when it has some, they go with it
        bool started = agentChats.Any(each => each.Parent == chat.Id);
        menu.Add(Said(started ? "agents_panel.chat_close_all" : "agents_panel.chat_close"), () => AskCloseAgentChat(chat), icon: "close");
        return menu;
    }

    /// <summary>A chat called something of somebody's own, what it was asked coming back when the name is left empty.</summary>
    private void RenameAgentChat(HexAgentChat chat)
    {
        BeginChatRename(chat);
    }

    /// <summary>A chat put on screen, what was being written for the one before left behind.</summary>
    private void OpenAgentChat(HexAgentChat chat)
    {
        // Picked from a narrow window's fleet, the middle comes back, the chat already open included
        FleetPicked();
        if (agentChat == chat)
            return;

        agentChat = chat;
        agentDraftContext = null;
        agentsInput.Text = string.Empty;
    }

    /// <summary>
    /// A chat closed when the person says so: at once when nothing in it is working or waiting, otherwise once they've said
    /// yes to how many agents that stops, closing can't be undone.
    /// </summary>
    private void AskCloseAgentChat(HexAgentChat chat)
    {
        List<HexAgentChat> family = FamilyOf(chat);
        int working = family.Count(each => each.Running || each.HasPending);
        if (working == 0)
        {
            CloseAgentChat(chat);
            return;
        }

        string yes = Said("agents_panel.close_go");
        string text = working == 1 ? Said("agents_panel.close_text_one") : Said("agents_panel.close_text", ("count", working.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Context.Windows.Ask(Said("agents_panel.close_title"), text, [Said("git.discard_no"), yes], answer =>
        {
            if (answer == yes && agentChats.Contains(chat))
                CloseAgentChat(chat);
        }, risky: yes);
    }

    /// <summary>A chat closed, its agent stopped, and the agents it started closed with it.</summary>
    private void CloseAgentChat(HexAgentChat chat)
    {
        foreach (HexAgentChat started in agentChats.Where(each => each.Parent == chat.Id).ToList())
            CloseAgentChat(started);

        StopAgent(chat, withStarted: true);
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
        if (agentsActivity is null)
            return;
        int working = 0;
        foreach (HexAgentChat chat in agentChats)
            if (chat.Running)
                working++;
        if (working == agentsWorkingShown)
            return;

        agentsWorkingShown = working;
        agentsActivity.Icon = working > 0 ? "robot~breathe" : "robot";
        agentsActivity.Tooltip = working > 0 ? Said("agents_panel.working_tip", ("count", working.ToString())) : Said("activity.agents");
    }
}
