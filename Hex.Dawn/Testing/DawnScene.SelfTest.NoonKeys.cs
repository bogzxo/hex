using Hex.Shell;

namespace Hex.Dawn;

// Agentic's keys pressed the way a person does. Alt with Page Down and Page Up going round the fleet in its order, Alt
// with Home up to the chat that started this one, Ctrl+Alt+. to the next that wants looking at, Ctrl+Alt+Shift+N back
// to Dawn, and the fleet's keys doing nothing in Dawn, where there's no fleet to go round.
internal sealed partial class DawnScene
{
    private void TestNoonKeys(HexPointerScript test)
    {
        HexAgentChat? first = null, child = null, last = null;

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            first = new HexAgentChat(++agentChatsMade, "noon_agent");
            first.Ask("lead the work");
            first.Done();
            child = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = first.Id, Depth = 1, Name = "Scout" };
            child.Ask("look round");
            child.Done();
            last = new HexAgentChat(++agentChatsMade, "noon_agent");
            last.Ask("on its own");
            last.Done();
            agentChats.Add(first);
            agentChats.Add(child);
            agentChats.Add(last);
            OpenAgentChat(first);
        });
        test.Settle(0.2f);

        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: in Dawn the fleet's keys leave the chat as it was", () => agentChat == first);

        test.Run(() => ShowFace(HexFace.Noon));
        test.Settle(0.3f);
        PressKeys(test, "Alt+PageDown");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Page Down goes to the next in the fleet, the one the first started", () => agentChat == child);
        PressKeys(test, "Alt+Home");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Home goes up to the chat that started it", () => agentChat == first);
        // The one before in the fleet, round from its end when the first is at its top (chats of other suites may be there)
        HexAgentChat? before = null;
        test.Run(() =>
        {
            List<HexAgentChat> order = FleetOrder();
            before = order[(order.IndexOf(first!) - 1 + order.Count) % order.Count];
        });
        PressKeys(test, "Alt+PageUp");
        test.Settle(0.2f);
        test.Check("noon keys: Alt+Page Up goes to the one before in the fleet, round from its end", () => agentChat == before && before != null);

        // One that finished while somebody looked elsewhere wants looking at
        test.Run(() =>
        {
            child!.Ask("one more thing");
            child.Say("done that too");
            child.Done();
        });
        test.Settle(0.2f);
        PressKeys(test, "Ctrl+Alt+.");
        test.Settle(0.2f);
        test.Check("noon keys: Ctrl+Alt+. goes to the next chat that wants looking at", () => agentChat == child);

        PressKeys(test, "Ctrl+Alt+Shift+N");
        test.Settle(0.3f);
        test.Check("noon keys: Ctrl+Alt+Shift+N goes back to Dawn", () => Face == HexFace.Dawn);

        test.Run(() =>
        {
            foreach (HexAgentChat? made in (HexAgentChat?[])[child, first, last])
            {
                if (made is not null)
                    agentChats.Remove(made);
            }
            agentChat = null;
        });
        test.Settle(0.2f);
    }
}
