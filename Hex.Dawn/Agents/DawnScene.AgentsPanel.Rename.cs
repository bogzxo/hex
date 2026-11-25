using System.Numerics;

using Horizon.Rendering;

namespace Hex.Dawn;

// A chat's card renamed where it is, its name turned into a box in place of the line it was on, the whole of it picked.
// Enter or a click anywhere else keeps what was typed, Escape leaves the name as it was, and nothing typed goes back to
// what the chat was asked. Its menu's Rename, F2 on the card or a double click on its name start it, the same on the
// Agents view's list and on Noon's fleet. The card keeps its size, the box sitting over the name's line, and the keys go
// back to the card after Enter or Escape. One box for the chat being renamed, put on its card again when the list is
// made again under it (a clock moving on another card, its own agent finishing).
internal sealed partial class DawnScene
{
    // The chat whose name is being typed, the box it's typed in and the name's line on the card the box is on now
    private HexAgentChat? chatRenaming;
    private ChatRenameBox? chatRenameBox;
    private Label? chatRenameHides;

    // The chat whose card takes the keys back once it's on screen again, after Enter or Escape
    private HexAgentChat? chatRenameRefocus;

    // The chat whose name was last clicked and how long ago by the UI's time, for a second click on it to be a double
    // click. The UI's time, the way the tree and the table count theirs: by the clock a frame that took long (the
    // window busy, the machine busy) used up the double click between two clicks a frame or two apart
    private (int Chat, float Since) chatTitlePress = (-1, float.MaxValue);

    // How soon a second click on a card's name has to come to rename it, in seconds
    private const float TITLE_DOUBLE_CLICK = 0.4f;

    /// <summary>A chat's card turned to renaming, its name a box on the next drawing of the list it's in.</summary>
    private void BeginChatRename(HexAgentChat chat)
    {
        if (chatRenaming is { } other && other != chat)
            EndChatRename(commit: true, refocus: false);

        chatRenaming = chat;
        chatRenameBox ??= new ChatRenameBox(this);
        ChatRenameBox box = chatRenameBox;
        box.Text = chat.Shown;
        box.Placeholder = chat.Title.Length > 0 ? chat.Title : Said("agents_panel.new_chat");
        box.Tooltip = Said("agents_panel.rename_message");
        box.Visible = true;
        box.Begin();
        agentsListDrawn = NOT_DRAWN;
        noonFleetDrawn = NOT_DRAWN;
    }

    /// <summary>
    /// The renaming over, what was typed kept or not. The card is made again with its name, and given the keys when it
    /// ended on a key rather than a click somewhere else, which keeps what it clicked.
    /// </summary>
    private void EndChatRename(bool commit, bool refocus)
    {
        if (chatRenaming is not { } chat || chatRenameBox is not { } box)
            return;

        chatRenaming = null;
        // Hidden here and taken off its card when it's next put on one, this runs inside the box's own update
        box.Visible = false;
        if (chatRenameHides is { } name)
            name.Visible = true;
        chatRenameHides = null;

        // The name as it was shown left alone, an automatic one isn't made a name of its own for being kept
        string typed = box.Text.Trim();
        if (commit && agentChats.Contains(chat) && typed != chat.Shown)
            chat.Rename(typed);

        chatRenameRefocus = refocus ? chat : null;
        agentsListDrawn = NOT_DRAWN;
        noonFleetDrawn = NOT_DRAWN;
    }

    /// <summary>
    /// What a card made for a chat has to do with renaming: the box put over its name while the chat is being renamed,
    /// and the keys given to it when the renaming just ended on a key.
    /// </summary>
    private void ChatCardMade(HexAgentChat chat, UIComponent root, AgentCardButton pick, Label name)
    {
        if (chatRenameRefocus == chat)
        {
            chatRenameRefocus = null;
            pick.FocusWhenShown = true;
        }

        if (chatRenaming != chat || chatRenameBox is not { } box)
            return;

        box.Parent?.Remove(box);
        root.Add(box);
        name.Visible = false;
        chatRenameHides = name;
        box.Again();
    }

    /// <summary>
    /// The box as wide as the card's name is cut to, at the width the card was put right at, less the few it starts left
    /// of the name. The width the cards are given is the panel's, wider than a card in the Agents view's list.
    /// </summary>
    private void FitChatRename(HexAgentChat chat, float room)
    {
        if (chatRenaming == chat && chatRenameBox is { } box)
            box.Size = new Vector2(MathF.Max(40.0f, room - 30.0f), CHAT_RENAME_TALL);
    }

    // How tall the box is, as tall as the name's line and the gaps round it without reaching the lines over and under
    private const float CHAT_RENAME_TALL = 22.0f;

    /// <summary>
    /// A click on a card: on its name a second time soon after the first, the name renamed. True when that's what it
    /// was, the click going no further.
    /// </summary>
    private bool ChatTitleClicked(HexAgentChat chat)
    {
        if (chatTitlePress.Chat == chat.Id && chatTitlePress.Since <= TITLE_DOUBLE_CLICK)
        {
            chatTitlePress = (-1, float.MaxValue);
            BeginChatRename(chat);
            return true;
        }

        chatTitlePress = (chat.Id, 0.0f);
        return false;
    }

    /// <summary>
    /// F2 on an agent's card: the one with the keys, or the chat on screen with nothing typing and its card shown, in
    /// Noon's fleet or the Agents view's list. True when a card was there to rename.
    /// </summary>
    private bool RenameAgentCardByKey()
    {
        if (compositor.Focus is AgentCardButton { Chat: var focused } && agentChats.Contains(focused))
        {
            BeginChatRename(focused);
            return true;
        }

        if (compositor.Focus is not null || agentChat is not { } chat)
            return false;

        bool shown = Face == Hex.Shell.HexFace.Noon ? noonFleet.Visible : Side == HexSide.Agents && agentsListScroll.Visible && agentsList.Visible;
        if (!shown)
            return false;

        BeginChatRename(chat);
        return true;
    }

    /// <summary>
    /// The button a card is, which knows its chat for F2, a second click on the card's name for renaming, and takes the
    /// keys once it's on screen when it's told to.
    /// </summary>
    private sealed class AgentCardButton(DawnScene scene, HexAgentChat chat) : Button
    {
        internal HexAgentChat Chat { get; } = chat;

        // The line of the card's name, a click on it being a click on the name
        internal Label? Title { get; set; }

        internal bool FocusWhenShown { get; set; }

        // The card this is the body of. The badges, the name and the close button sit over the button as its siblings, and
        // the pointer being on any of them is on the card, which lights it
        internal UIComponent? Card { get; set; }

        protected override bool PointerLit
        {
            get
            {
                if (base.PointerLit)
                    return true;
                for (UIComponent? at = Module?.Compositor.HoveredComponent; at is not null && Card is not null; at = at.Parent)
                {
                    if (at == Card)
                        return true;
                }

                return false;
            }
        }

        protected override void OnTextInput(char character)
        {
            switch (character)
            {
                case UIKeyboard.UP: scene.MoveChatListFocus(this, -1); break;
                case UIKeyboard.DOWN: scene.MoveChatListFocus(this, 1); break;
                case UIKeyboard.LEFT: scene.MoveChatListFocus(this, -1, headOnly: true); break;
                default: base.OnTextInput(character); break;
            }
        }

        // Whether the click going on was pressed on the card's name, and not by a key
        private bool onTitle;

        protected override void OnPointerDown(Vector2 point)
        {
            base.OnPointerDown(point);
            onTitle = Title is { Visible: true } title && point.Y >= MathF.Min(title.Bounds.Min.Y, title.Bounds.Max.Y) - 3.0f
                && point.Y <= MathF.Max(title.Bounds.Min.Y, title.Bounds.Max.Y) + 3.0f;
        }

        protected override void OnClick()
        {
            bool title = onTitle;
            onTitle = false;
            if (title && scene.ChatTitleClicked(Chat))
                return;
            if (!title)
                scene.chatTitlePress = (-1, float.MaxValue);
            base.OnClick();
        }

        protected override void Update(float dt)
        {
            base.Update(dt);

            // The time since its name was clicked goes by on the card of the chat that was clicked
            if (scene.chatTitlePress.Chat == Chat.Id && scene.chatTitlePress.Since < float.MaxValue)
                scene.chatTitlePress.Since += dt;
            if (FocusWhenShown && Module is not null)
            {
                FocusWhenShown = false;
                Focus();
            }
        }
    }

    /// <summary>
    /// The box a card's name is typed in, over the line of the name. It takes the keys once it's on screen, all of it
    /// picked the first time, and says when the keys went somewhere else, which keeps what was typed.
    /// </summary>
    private sealed class ChatRenameBox : TextBox
    {
        private readonly DawnScene scene;

        // Whether it's to take the keys once it's on screen, and pick the whole name when it does
        private bool wantsFocus, selectOnFocus;

        // Whether it had the keys, so their going elsewhere is a click away
        private bool hadFocus;

        internal ChatRenameBox(DawnScene scene)
        {
            this.scene = scene;
            Anchor = Origin.TopLeft;
            Pivot = Origin.TopLeft;
            // Its words where the name's were, the name's line less the box's padding
            Position = new Vector2(6, -(CARD_TITLE - 4.0f));
            Padding = new UIEdges(4.0f, 2.0f);
            TextScale = CARD_NAME;
            MaxLength = 200;
            Size = new Vector2(160, CHAT_RENAME_TALL);
            OnSubmitted = _ => scene.EndChatRename(commit: true, refocus: true);
        }

        /// <summary>Renaming started, the keys taken and all of it picked once it's on a card.</summary>
        internal void Begin()
        {
            wantsFocus = selectOnFocus = true;
            hadFocus = false;
        }

        /// <summary>Put on a card made again, the keys taken back without picking what was typed again.</summary>
        internal void Again() => wantsFocus = Visible;

        protected override bool OnEscape()
        {
            scene.EndChatRename(commit: false, refocus: true);
            return true;
        }

        protected override void Update(float dt)
        {
            base.Update(dt);
            if (!Visible)
                return;

            if (wantsFocus && Module is not null)
            {
                wantsFocus = false;
                Focus();
                if (selectOnFocus)
                {
                    selectOnFocus = false;
                    Select(0, Text.Length);
                }
            }

            if (IsFocused)
                hadFocus = true;
            else if (hadFocus && !wantsFocus)
            {
                hadFocus = false;
                scene.EndChatRename(commit: true, refocus: false);
            }
        }
    }
}
