using Hex.Shell;

namespace Hex.Dawn;

// Agentic's keys pressed the way a person does. Alt with Page Down and Page Up going to the card under and over the one
// on screen, as the fleet draws them (newest first, three chats of their own so a walk of its own order shows), the
// fleet's filter narrowing where they go, Alt with Home up to the chat that started this one, Ctrl+Alt+. to the next
// that wants looking at whatever the filter hides, Ctrl+Alt+Shift+N back to Dawn, and the fleet's keys doing nothing in
// Dawn, where there's no fleet to go round.
internal sealed partial class DawnScene
{
    private void TestNoonKeys(HexPointerScript test)
    {
        HexAgentChat? first = null, child = null, middle = null, last = null;
        const string TAG = "NoonKeysTag";

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            first = new HexAgentChat(++agentChatsMade, "noon_agent");
            first.Ask("lead the work");
            first.Done();
            child = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = first.Id, Depth = 1, Name = "Scout" };
            child.Ask("look round");
            child.Done();
            middle = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = $"{TAG} middle" };
            middle.Ask("on its own");
            middle.Done();
            last = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = $"{TAG} last" };
            last.Ask("on its own too");
            last.Done();
            agentChats.Add(first);
            agentChats.Add(child);
            agentChats.Add(middle);
            agentChats.Add(last);
            OpenAgentChat(first);
        });
        test.Settle(0.2f);

        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: in Dawn the fleet's keys leave the chat as it was", () => agentChat == first);

        test.Run(() =>
        {
            ShowFace(HexFace.Noon);
            noonFleetFilter.Text = string.Empty;
            noonWait = 0;
        });
        test.Settle(0.4f);

        // The chat on each card of the fleet as it's drawn, top to bottom, and the one under or over a chat's card
        HexAgentChat? Card(HexAgentChat chat, int by)
        {
            List<(HexAgentChat Chat, int Step)> shown = FleetShown();
            int at = shown.FindIndex(each => each.Chat == chat);
            return at < 0 ? null : shown[((at + by) % shown.Count + shown.Count) % shown.Count].Chat;
        }

        test.Check("noon keys: the fleet draws the newest first, a started chat under its starter", () =>
            FleetShown().Select(each => each.Chat).Where(chat => chat == first || chat == child || chat == middle || chat == last)
                .SequenceEqual([last!, middle!, first!, child!]) && noonFleetRows.Children.Count == FleetShown().Count);

        // Picked with the pointer, the middle chat's card clicked
        test.Click(() => chrome.ToWorld(noonFleetRows.Children[FleetShown().FindIndex(each => each.Chat == middle)].Bounds.Center));
        test.Settle(0.2f);
        test.Check("noon keys: a click on a card in the fleet opens its chat", () => agentChat == middle);

        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Page Down goes to the card under the one clicked, not over it", () => agentChat == first && Card(middle!, 1) == first);
        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Page Down goes on to the one the first started, a step in under it", () => agentChat == child);
        PressKeys(test, "Alt+Home");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Home goes up to the chat that started it", () => agentChat == first);
        PressKeys(test, "Alt+PageUp");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Page Up goes to the card over this one", () => agentChat == middle && Card(first!, -1) == middle);

        // The fleet's filter narrowing it to two, the keys going round only what it shows, round from the end
        test.Run(() =>
        {
            noonFleetFilter.Text = TAG;
            noonWait = 0;
            OpenAgentChat(last!);
        });
        test.Settle(0.4f);
        test.Check("noon keys: the filter leaves the two it fits in the fleet", () =>
            FleetShown().Select(each => each.Chat).SequenceEqual([last!, middle!]) && noonFleetRows.Children.Count == 2);
        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: filtered, Alt+Page Down goes to the next card shown", () => agentChat == middle);
        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: filtered, Alt+Page Down goes round to the top, past the chats it hides", () => agentChat == last);
        PressKeys(test, "Alt+PageUp");
        test.Settle(0.2f);
        test.Check("noon keys: filtered, Alt+Page Up goes round from the top to the last card shown", () => agentChat == middle);

        // One that finished while somebody looked elsewhere wants looking at, even when the filter hides it
        test.Run(() =>
        {
            child!.Ask("one more thing");
            child.Say("done that too");
            child.Done();
        });
        test.Settle(0.2f);
        PressKeys(test, "Ctrl+Alt+.");
        test.Settle(0.2f);
        test.Check("noon keys: Ctrl+Alt+. goes to the next chat that wants looking at, whatever the filter hides", () => agentChat == child);
        test.Run(() =>
        {
            noonFleetFilter.Text = string.Empty;
            noonWait = 0;
        });
        test.Settle(0.3f);

        PressKeys(test, "Ctrl+Alt+Shift+N");
        test.Settle(0.3f);
        test.Check("noon keys: Ctrl+Alt+Shift+N goes back to Dawn", () => Face == HexFace.Dawn);

        test.Run(() =>
        {
            noonFleetFilter.Text = string.Empty;
            foreach (HexAgentChat? made in (HexAgentChat?[])[child, first, middle, last])
            {
                if (made is not null)
                    agentChats.Remove(made);
            }
            agentChat = null;
        });
        test.Settle(0.2f);
    }
}
