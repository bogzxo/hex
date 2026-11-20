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
            Style = "button_flat",
            Align = Origin.Left,
            Anchor = Origin.Top,
            Pivot = Origin.Top,
            Fill = UIFill.Horizontal,
            Size = new Vector2(0, CHATS_HEAD),
            LabelScale = 0.125f,
            Animated = false
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

    /// <summary>
    /// Every chat in the order the list and the fleet show them (HexAgentListOrder): families together, those waiting on
    /// the person first, then those working, then those that finished a moment ago, then the finished, the most recently
    /// active first in each. The finished come last whether they're folded away or not, ChatsShown leaves them out.
    /// </summary>
    /// <remarks>
    /// Made again only when what the order goes by moved (the chats, who started whom, how each is doing, a finished one's
    /// few minutes running out), the same list handed back otherwise, so it's read and never changed by whoever asks.
    /// </remarks>
    private List<(HexAgentChat Chat, int Step)> ChatsInOrder()
    {
        long now = Environment.TickCount64;
        bool lift = !FinishedShown;
        long key = 17 + (lift ? (agentChat?.Id ?? 0) * 7919L + 1 : 0);
        for (int i = 0; i < agentChats.Count; i++)
        {
            HexAgentChat chat = agentChats[i];
            var facts = HexChatFacts.Of(chat, NeedsYou(chat));
            key = (key * 31 + chat.Id) * 31 + (chat.Parent ?? -1);
            key = key * 31 + (int)HexAgentListOrder.BandOf(facts, now);
            key = key * 31 + HexAgentListOrder.ActiveAt(facts);
            key = key * 31 + (facts.Unread ? 1 : 0);
        }
        if (key == chatsOrderedFor && chatsOrdered.Count == agentChats.Count)
            return chatsOrdered;

        chatsOrderedFor = key;
        chatFacts.Clear();
        foreach (HexAgentChat chat in agentChats)
            chatFacts.Add(HexChatFacts.Of(chat, NeedsYou(chat)));
        HexAgentListOrder.Order(chatFacts, now, agentChat?.Id ?? -1, lift, chatPlaces);

        // New lists rather than the old ones filled again, whoever holds the last one is reading it
        var ordered = new List<(HexAgentChat, int)>(chatPlaces.Count);
        var shown = new List<(HexAgentChat, int)>(chatPlaces.Count);
        var bands = new List<HexChatBand>(chatPlaces.Count);
        int folded = 0, foldedUnread = 0;
        foreach (HexChatPlace place in chatPlaces)
        {
            HexAgentChat chat = agentChats[place.Index];
            ordered.Add((chat, place.Step));
            bands.Add(place.Band);
            if (place.Band != HexChatBand.Finished)
                shown.Add((chat, place.Step));
            else
            {
                folded++;
                foldedUnread += chat.Unread ? 1 : 0;
            }
        }

        chatsOrdered = ordered;
        chatsMain = shown;
        chatBands = bands;
        chatsFinished = (folded, foldedUnread);
        return ordered;
    }

    /// <summary>
    /// The chats on screen in the order shown: every one while the finished group is open, the finished left out while
    /// it's folded (the chat open never, it's lifted out of the finished band then).
    /// </summary>
    private List<(HexAgentChat Chat, int Step)> ChatsShown()
    {
        List<(HexAgentChat Chat, int Step)> all = ChatsInOrder();
        return FinishedShown ? all : chatsMain;
    }

    /// <summary>The band of the chat at a place of ChatsInOrder, its family's.</summary>
    private HexChatBand BandAt(int at) => at >= 0 && at < chatBands.Count ? chatBands[at] : HexChatBand.Recent;

    /// <summary>Whether a chat waits on the person: an approval or a question, a failure nobody saw, or messages it hasn't read while it's idle.</summary>
    private static bool NeedsYou(HexAgentChat chat) =>
        chat.HasPending || (chat.Failed && chat.Unread) || (chat.UnreadMailCount() > 0 && !chat.Running);

    /// <summary>
    /// Whether the finished chats are on screen: the person opened their group (kept with the workspace), something is
    /// typed in the fleet's filter (it looks through every chat), or the fleet has the whole window to itself.
    /// </summary>
    private bool FinishedShown =>
        chatsFinishedOpen || (Face == HexFace.Noon && ((noonFleetFilter?.Text.Trim().Length ?? 0) > 0 || (noonNarrow && noonFleetOpened)));

    /// <summary>The finished group opened or folded, both lists drawn again for it and the choice kept with the workspace.</summary>
    private void ToggleFinished()
    {
        chatsFinishedOpen = !chatsFinishedOpen;
        agentsListDrawn = NOT_DRAWN;
        noonFleetDrawn = NOT_DRAWN;
        noonWait = 0.0f;
        Engine.WindowManager.RequestFrames();
    }

    // The chats in order as they were last worked out and what they were worked out from, the ones not folded away, the
    // band of each and how many are folded (and of those, not read)
    private List<(HexAgentChat Chat, int Step)> chatsOrdered = [], chatsMain = [];
    private List<HexChatBand> chatBands = [];
    private (int Count, int Unread) chatsFinished;
    private long chatsOrderedFor;
    private readonly List<HexChatFacts> chatFacts = [];
    private readonly List<HexChatPlace> chatPlaces = [];

    // Whether the person opened the finished group, kept with the workspace's chats
    private bool chatsFinishedOpen;

    /// <summary>
    /// The finished group's head, the same in the list and the fleet: its name after a chevron at the left, set like the
    /// other groups' headings and lined up with them, and at the right how many nobody read (in the accent, with its dot)
    /// and how many it holds. The whole row is the button, its hover and press the flat button's. Over it, when nothing
    /// is working or waiting, a quiet line saying so, so a list folded down to its head doesn't look broken.
    /// </summary>
    /// <param name="inset">How far in its words start, the fleet's headings start at its edge and the list's cards ten in.</param>
    private UIComponent FinishedHead(bool alone = false, float inset = 10.0f)
    {
        var (count, unread) = chatsFinished;
        bool open = FinishedShown;
        var holder = new StackPanel { Fill = UIFill.Horizontal, Spacing = 0 };
        // A breath over it, apart from the cards above as the other groups' headings are
        holder.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, FINISHED_GAP), Color = Vector4.Zero });
        if (alone)
            holder.Add(new Label { Text = Said("agents_panel.finished_none_working"), TextScale = 0.125f, Dim = true, Align = Origin.Left, Fill = UIFill.Horizontal, Size = new Vector2(0, FINISHED_ALONE), Position = new Vector2(inset, 0) });

        var row = holder.Add(new Panel { Fill = UIFill.Horizontal, Size = new Vector2(0, FINISHED_HEAD), Color = Vector4.Zero });
        row.Add(new Button
        {
            Style = "button_flat", Fill = UIFill.Both, Label = string.Empty, Animated = false,
            Tooltip = Said(open ? "agents_panel.finished_fold" : "agents_panel.finished_open"), OnPressed = ToggleFinished
        });
        row.Add(new Label
        {
            Text = $"$({(open ? "chevron-down" : "chevron-right")}) {Said("agents_panel.finished_head")}", TextScale = 0.125f, Dim = true, Bold = true,
            Anchor = Origin.Left, Pivot = Origin.Left, Position = new Vector2(inset, 0), Align = Origin.Left
        });
        string many = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.Add(new Badge { Text = many, Tone = UIBadgeTone.Neutral, TextScale = 0.11f, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8, 0) });
        if (unread > 0)
        {
            // Left of the count, measured off its words, a pill's ends and its dot
            float countWidth = (Skin?.Font.Measure(many, 0.11f, markup: false).X ?? 8.0f) + 14.0f;
            row.Add(new Badge
            {
                Text = Said("agents_panel.finished_unread", ("count", unread.ToString(System.Globalization.CultureInfo.InvariantCulture))), Tone = UIBadgeTone.Accent, Dot = true,
                TextScale = 0.11f, Anchor = Origin.Right, Pivot = Origin.Right, Position = new Vector2(-8 - countWidth - 6, 0)
            });
        }
        return holder;
    }

    /// <summary>How tall the finished group's head is, with its line saying nothing works over it when that's said.</summary>
    private static float FinishedTall(bool alone) => FINISHED_GAP + FINISHED_HEAD + (alone ? FINISHED_ALONE : 0.0f);

    // How tall the line over a head that's all there is to the list is, and the gap over the head
    private const float FINISHED_ALONE = 26.0f, FINISHED_GAP = 6.0f;

    // How tall the finished group's head is
    private const float FINISHED_HEAD = 24.0f;

    /// <summary>
    /// The cards of a list just made again slid from where they were to where they are now, a new one sliding in, so a
    /// chat moving up as it starts working or down as it finishes is seen to move rather than jump. Where each card was is
    /// kept by its chat from one making to the next, measured down from the list's top.
    /// </summary>
    private void SlideCards(Dictionary<int, float> was, List<(int Chat, UIComponent Card, float Depth)> now)
    {
        float time = AgentsMotion(AGENTS_SLIDE);
        bool first = was.Count == 0;
        // Those coming in together (a group opened) one a beat after another, a few at the most
        int coming = 0;
        foreach (var (chat, card, depth) in now)
        {
            if (time <= 0.0f || first)
                continue;
            if (!was.TryGetValue(chat, out float before))
                SlideRowIn(card, Math.Min(coming++, 5) * 0.035f);
            else if (MathF.Abs(before - depth) > 0.5f)
            {
                // Drawn where it was and eased to where it is, up being plus
                card.VisualOffset = new Vector2(0, depth - before);
                card.TweenOffset(Vector2.Zero, time).SetEasing(Horizon.Core.Tweening.Easing.OutCubic);
            }
        }

        was.Clear();
        foreach (var (chat, _, depth) in now)
            was[chat] = depth;
        now.Clear();
    }

    // Where each card of the list and the fleet was when they were last made, and the cards being made
    private readonly Dictionary<int, float> agentsListPlaces = [], noonFleetPlaces = [];
    private readonly List<(int Chat, UIComponent Card, float Depth)> cardsPlaced = [];

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
        // The order's own key (ChatsInOrder) for the bands and the finished group's choice, the cards' for what's on them
        ChatsInOrder();
        long said = ((((AgentCardsKey(ChatsShown()) * 31 + (wanted ? 1 : 0)) * 31 + (agentsListOpen ? 1 : 0)) * 31 + (long)most) * 31 + working) * 31 + (systems ? 1 : 0);
        said = (said * 31 + chatsOrderedFor) * 31 + (FinishedShown ? 1 : 0);
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
            // The working and the just finished, then the finished group's head, then the finished while it's open. Each
            // card slides from where it was, a chat that started working moving up rather than jumping there
            float depth = 0.0f, tall = 0.0f;
            bool shown = FinishedShown, headed = false;
            if (agentsListOpen)
            {
                for (int i = 0; i < chats.Count; i++)
                {
                    var (chat, step) = chats[i];
                    bool finished = BandAt(i) == HexChatBand.Finished;
                    if (finished && !headed)
                    {
                        headed = true;
                        bool alone = i == 0;
                        agentsListRows.Add(FinishedHead(alone));
                        depth += FinishedTall(alone) + agentsListRows.Spacing;
                    }
                    if (finished && !shown)
                        continue;
                    // A finished one's name quiet, the working ones read first; one waiting on the person with its rail
                    AgentCard card = AgentCardOf(chat, step, closable: true, quiet: finished && chat != agentChat, rail: BandAt(i) == HexChatBand.NeedsYou && NeedsYou(chat));
                    agentsListRows.Add(card.Root);
                    chatRowFits.Add(card.Refresh);
                    cardsPlaced.Add((chat.Id, card.Root, depth));
                    depth += AGENT_CARD + agentsListRows.Spacing;
                }
                // As many cards as the list shows before it scrolls, the finished group's head as tall as it is
                tall = MathF.Min(depth, most * (AGENT_CARD + 1.0f));
                SlideCards(agentsListPlaces, cardsPlaced);
            }

            foreach (Action<float> refresh in chatRowFits)
                refresh(width);
            float listed = tall;
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

        ShowDraftOf(chat);
    }

    // What the person was writing in each chat and not sent yet, the code it was about with it, put back when the chat is
    private readonly Dictionary<HexAgentChat, (string Text, AgentDraftContext? Context)> agentDrafts = [];

    /// <summary>
    /// A chat on screen in place of the one before, what was being written for that one kept with it and what was being
    /// written for this one put back in the box, the caret at its end.
    /// </summary>
    private void ShowDraftOf(HexAgentChat? chat)
    {
        if (agentChat is not null && agentChats.Contains(agentChat))
        {
            if (agentsInput.Text.Length > 0 || agentDraftContext is not null)
                agentDrafts[agentChat] = (agentsInput.Text, agentDraftContext);
            else
                agentDrafts.Remove(agentChat);
        }

        agentChat = chat;
        if (chat is not null && agentDrafts.Remove(chat, out var draft))
        {
            agentDraftContext = draft.Context;
            agentsInput.Text = draft.Text;
            string[] lines = draft.Text.Split('\n');
            agentsInput.GoTo(lines.Length - 1, lines[^1].Length);
        }
        else
        {
            agentDraftContext = null;
            agentsInput.Text = string.Empty;
        }
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
        agentDrafts.Remove(chat);
        if (agentChat == chat)
            ShowDraftOf(chat.Parent is { } parent && ChatById(parent) is { } starter ? starter : agentChats.LastOrDefault());
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
