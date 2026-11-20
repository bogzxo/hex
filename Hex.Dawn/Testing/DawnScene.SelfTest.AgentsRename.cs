using Hex.Shell;

namespace Hex.Dawn;

// A chat's card renamed in place the way a person does it. Its menu's Rename turns the name into a box on the card, all of
// it picked and the card as tall as it was, typing and Enter keep the name and give the keys back to the card, F2 there
// opens it again and Escape leaves the name as it was, an empty box goes back to what the chat was asked, a double click
// on the name opens it and a click on another card keeps what was typed. The same in Agentic's fleet.
internal sealed partial class DawnScene
{
    private void TestAgentsRename(HexPointerScript test)
    {
        HexAgentChat? one = null, two = null;

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            ShowAgentsPanel();
            one = new HexAgentChat(++agentChatsMade, "rename_agent");
            one.Ask("tidy the explorer");
            one.Done();
            two = new HexAgentChat(++agentChatsMade, "rename_agent");
            two.Ask("look at the tests");
            two.Done();
            agentChats.Add(one);
            agentChats.Add(two);
            OpenAgentChat(one);
        });
        test.Settle(0.4f);

        // The card the box is on, and the card of a chat in the Agents view's list
        UIComponent? BoxCard() => chatRenameBox?.Parent;
        UIComponent? CardOf(HexAgentChat chat) => agentsListRows.Children.FirstOrDefault(card => Descendants(card).OfType<AgentCardButton>().Any(pick => pick.Chat == chat));
        bool Open() => chatRenameBox is { Visible: true, IsFocused: true } box && box.Parent is not null && chatRenaming is not null;
        void Type(string text) => test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed(text)));

        test.Run(() => RenameAgentChat(one!));
        test.Settle(0.3f);
        test.Check("agents rename: the menu's Rename turns the card's name into a box in place, focused", () =>
            Open() && BoxCard() == CardOf(one!) && BoxCard()!.Parent == agentsListRows);
        test.Check("agents rename: the whole name is picked, the card as tall as it was", () =>
            chatRenameBox!.Selection == one!.Shown && MathF.Abs(BoxCard()!.Bounds.Height - AGENT_CARD) < 1.0f);
        test.Check("agents rename: the name's line is hidden under the box", () =>
            Descendants(BoxCard()!).OfType<Label>().All(label => !label.Visible || label.Text != UIRichText.Literal(one!.Shown)));

        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "agents_rename_dawn.png"));
        });
        test.Wait();
        Type("Explorer tidy");
        PressKeys(test, "Enter");
        test.Settle(0.3f);
        test.Check("agents rename: Enter keeps what was typed", () => one!.Name == "Explorer tidy" && chatRenaming is null);
        test.Check("agents rename: the keys go back to the card after", () => compositor.Focus is AgentCardButton { Chat: var focused } && focused == one);

        PressKeys(test, "F2");
        test.Settle(0.3f);
        test.Check("agents rename: F2 on the card opens the box again", () => Open() && chatRenaming == one);
        Type("something else");
        PressKeys(test, "Escape");
        test.Settle(0.3f);
        test.Check("agents rename: Escape leaves the name as it was", () => one!.Name == "Explorer tidy" && chatRenaming is null && compositor.Focus is AgentCardButton);

        PressKeys(test, "F2");
        test.Settle(0.3f);
        PressKeys(test, "Backspace Enter");
        test.Settle(0.3f);
        test.Check("agents rename: nothing typed goes back to what it was asked", () => one!.Name is null && one.Shown == one.Title);

        // A double click on the name, and a click on the other card keeping what was typed
        Label? TitleOf(HexAgentChat chat) => CardOf(chat) is { } card ? Descendants(card).OfType<AgentCardButton>().First().Title : null;
        test.Click(() => chrome.ToWorld(TitleOf(one!)!.Bounds.Center));
        test.Click(() => chrome.ToWorld(TitleOf(one!)!.Bounds.Center));
        test.Settle(0.3f);
        test.Check("agents rename: a double click on the name opens the box", () => Open() && chatRenaming == one);
        Type("Clicked away");
        test.Click(() => chrome.ToWorld(CardOf(two!)!.Bounds.Center));
        test.Settle(0.4f);
        test.Check("agents rename: a click on another card keeps what was typed", () => one!.Name == "Clicked away" && chatRenaming is null && agentChat == two);

        // Agentic's fleet, F2 on the chat on screen with nothing typing
        test.Run(() =>
        {
            ShowFace(HexFace.Noon);
            noonFleetFilter.Text = string.Empty;
            noonWait = 0;
            compositor.Focus = null;
        });
        test.Settle(0.4f);
        PressKeys(test, "F2");
        test.Settle(0.4f);
        test.Check("agents rename: F2 in Agentic opens the box on the fleet's card", () =>
            Open() && chatRenaming == two && BoxCard() is { } card && Descendants(noonFleetRows).Contains(card) && MathF.Abs(card.Bounds.Height - AGENT_CARD) < 1.0f);
        Type("Fleet named");
        test.Wait();
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "agents_rename_noon.png"));
        });
        test.Wait();
        PressKeys(test, "Enter");
        test.Settle(0.3f);
        test.Check("agents rename: Enter in the fleet keeps it", () => two!.Name == "Fleet named" && chatRenaming is null);

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            foreach (HexAgentChat? made in (HexAgentChat?[])[one, two])
            {
                if (made is not null)
                    agentChats.Remove(made);
            }
            agentChat = null;
        });
        test.Settle(0.2f);
    }
}
