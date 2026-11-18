using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Agentic's fleet. A chat that started two others is a block of three, the two under it a step in, the totals over the
// fleet count them and the one working, a card says what it's doing, and closing the one at the top closes the family.
internal sealed partial class DawnScene
{
    private void TestNoonFleet(HexPointerScript test)
    {
        HexAgentChat? lead = null, scout = null, builder = null;

        test.Run(() =>
        {
            lead = new HexAgentChat(++agentChatsMade, "noon_agent") { Model = "claude-opus-5-5" };
            lead.Ask("coordinate the fleet test");
            scout = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = lead.Id, Depth = 1, Name = "Scout" };
            scout.Ask("look round");
            scout.Done();
            builder = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = lead.Id, Depth = 1, Name = "Builder" };
            builder.Ask("build it");
            builder.Done();
            agentChats.Add(lead);
            agentChats.Add(scout);
            agentChats.Add(builder);
            OpenAgentChat(lead);
            ShowFace(HexFace.Noon);
            noonWait = 0;
        });
        test.Settle(0.5f);

        // The card of a chat in the fleet, by where the chat is in its order
        UIComponent? CardOf(HexAgentChat chat)
        {
            int at = ChatsInOrder().FindIndex(each => each.Chat == chat);
            return at >= 0 && at < noonFleetRows.Children.Count ? noonFleetRows.Children[at] : null;
        }

        test.Check("noon fleet: the two a chat started come right after it in the fleet", () =>
            ChatsInOrder().Select(each => each.Chat).SkipWhile(chat => chat != lead).Take(3).SequenceEqual([lead!, scout!, builder!]));
        test.Check("noon fleet: the ones it started are a step in under it", () =>
            CardOf(lead!) is { } top && CardOf(scout!) is { } first && CardOf(builder!) is { } second
            && first.Padding.Left > top.Padding.Left + 4 && MathF.Abs(second.Padding.Left - first.Padding.Left) < 1);
        test.Check("noon fleet: the cards say who they are", () =>
            CardOf(scout!) is { } card && Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Scout", StringComparison.Ordinal)));
        test.Check("noon fleet: the totals over the fleet count every agent and the one working", () =>
            noonFleetSummary.Text.Contains($"{agentChats.Count} agents", StringComparison.Ordinal) && noonFleetSummary.Text.Contains("1 running", StringComparison.Ordinal));

        test.Run(() =>
        {
            lead!.Done();
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon fleet: and none once it's done", () => noonFleetSummary.Text.Contains("0 running", StringComparison.Ordinal));

        test.Run(() => CloseAgentChat(lead!));
        test.Settle(0.4f);
        test.Check("noon fleet: closing the one at the top closes the ones it started", () =>
            ChatById(lead!.Id) is null && ChatById(scout!.Id) is null && ChatById(builder!.Id) is null);
        test.Run(() => ShowFace(HexFace.Dawn));
        test.Settle(0.2f);
    }
}
