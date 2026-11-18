using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The side bar's Agents view and Agentic are one view of the one chat. A chat picked in the side bar is the one in
// Agentic, one picked in Agentic's fleet is the one in the side bar after, and a question answered in Agentic is
// answered in the side bar too, no longer wanting looking at.
internal sealed partial class DawnScene
{
    private void TestNoonPanel(HexPointerScript test)
    {
        HexAgentChat? asking = null, other = null;
        string? told = null;

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            asking = new HexAgentChat(++agentChatsMade, "noon_agent");
            asking.Ask("ask me something");
            asking.Question("Which way?", ["Left", "Right"], 1, 300, answer => told = answer);
            other = new HexAgentChat(++agentChatsMade, "noon_agent");
            other.Ask("another chat");
            other.Say("the other one");
            other.Done();
            agentChats.Add(asking);
            agentChats.Add(other);
            ShowAgentsPanel();
            OpenAgentChat(asking);
        });
        test.Settle(0.4f);
        test.Check("noon panel: a question waiting in the side bar has its answers to press", () =>
            Descendants(agentsScroll).OfType<Button>().Any(button => button.Label.StartsWith("Right", StringComparison.Ordinal)) && asking!.Pending.Any());

        test.Run(() =>
        {
            ShowFace(HexFace.Noon);
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon panel: the chat picked in the side bar is the one in Agentic", () => agentChat == asking && agentsView.Parent == noonCenter);
        test.Run(() => Descendants(agentsScroll).OfType<Button>().First(button => button.Label == "Left").OnPressed!());
        test.Settle(0.4f);
        test.Check("noon panel: answered in Agentic, the agent is told and nothing waits any more", () => told == "Left" && !asking!.Pending.Any());

        // Picked in the fleet, by its card's press
        test.Run(() =>
        {
            int at = ChatsInOrder().FindIndex(each => each.Chat == other);
            Descendants(noonFleetRows.Children[at]).OfType<Button>().First().OnPressed!();
        });
        test.Settle(0.3f);
        test.Check("noon panel: a chat picked in the fleet is the one in the middle", () => agentChat == other);
        test.Run(() => ShowFace(HexFace.Dawn));
        test.Settle(0.4f);
        test.Check("noon panel: and the one in the side bar back in Dawn", () =>
            agentChat == other && agentsView.Parent == left && Descendants(agentsScroll).OfType<Label>().Any(label => label.Text.Contains("the other one", StringComparison.Ordinal)));
        test.Run(() => OpenAgentChat(asking!));
        test.Settle(0.4f);
        test.Check("noon panel: the question answered in Agentic says its answer in the side bar", () =>
            Descendants(agentsScroll).OfType<Label>().Any(label => label.Text.Contains(Said("agents_panel.question_answered", ("answer", "Left")), StringComparison.Ordinal)));

        test.Run(() =>
        {
            CloseAgentChat(asking!);
            CloseAgentChat(other!);
        });
        test.Settle(0.2f);
    }
}
