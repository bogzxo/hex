using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The order the Agents view's list and Agentic's fleet show chats in: waiting on the person, then working, then just
/// finished, then the finished folded away, most recently active first in each, families kept together and the chat open
/// never folded.
/// </summary>
public sealed class HexAgentListOrderTests
{
    private const long NOW = 10_000_000;

    private static HexChatFacts Working(int id, long started, int? parent = null) => new(id, parent, true, false, true, false, started, 0);
    private static HexChatFacts Asking(int id, long started, int? parent = null) => new(id, parent, true, true, true, false, started, 0);
    private static HexChatFacts Done(int id, long ended, int? parent = null, bool unread = false) => new(id, parent, false, false, true, unread, ended - 5_000, ended);
    private static HexChatFacts Fresh(int id) => new(id, null, false, false, false, false, 0, 0);

    private static List<HexChatPlace> Order(IReadOnlyList<HexChatFacts> chats, int open = -1, bool lift = true)
    {
        var into = new List<HexChatPlace>();
        HexAgentListOrder.Order(chats, NOW, open, lift, into);
        return into;
    }

    private static int[] Ids(IReadOnlyList<HexChatFacts> chats, List<HexChatPlace> order) => [.. order.Select(place => chats[place.Index].Id)];

    [Fact]
    public void WaitingOnThePersonThenWorkingThenJustFinishedThenFinished()
    {
        HexChatFacts[] chats =
        [
            Done(1, NOW - 60 * 60_000),
            Working(2, NOW - 1_000),
            Done(3, NOW - 30_000),
            Asking(4, NOW - 50_000),
        ];
        List<HexChatPlace> order = Order(chats);

        Assert.Equal([4, 2, 3, 1], Ids(chats, order));
        Assert.Equal([HexChatBand.NeedsYou, HexChatBand.Working, HexChatBand.Recent, HexChatBand.Finished], order.Select(place => place.Band));
    }

    [Fact]
    public void MostRecentlyActiveFirstWithinABand()
    {
        HexChatFacts[] chats = [Working(1, NOW - 9_000), Working(2, NOW - 1_000), Working(3, NOW - 5_000), Done(4, NOW - 90 * 60_000), Done(5, NOW - 60 * 60_000)];

        Assert.Equal([2, 3, 1, 5, 4], Ids(chats, Order(chats)));
    }

    [Fact]
    public void AFinishedChatStaysAFewMinutesThenFolds()
    {
        HexChatFacts just = Done(1, NOW - HexAgentListOrder.RECENT_MS + 1_000);
        HexChatFacts past = Done(1, NOW - HexAgentListOrder.RECENT_MS - 1_000);

        Assert.Equal(HexChatBand.Recent, HexAgentListOrder.BandOf(just, NOW));
        Assert.Equal(HexChatBand.Finished, HexAgentListOrder.BandOf(past, NOW));
    }

    [Fact]
    public void AnUnreadFinishedChatStaysLongerButNotForever()
    {
        Assert.Equal(HexChatBand.Recent, HexAgentListOrder.BandOf(Done(1, NOW - HexAgentListOrder.RECENT_MS - 1_000, unread: true), NOW));
        Assert.Equal(HexChatBand.Finished, HexAgentListOrder.BandOf(Done(1, NOW - HexAgentListOrder.UNREAD_RECENT_MS - 1_000, unread: true), NOW));
    }

    [Fact]
    public void AChatReadBackFromAnEarlierRunIsFinishedAtOnce()
    {
        var restored = new HexChatFacts(1, null, false, false, true, true, 0, 0);

        Assert.Equal(HexChatBand.Finished, HexAgentListOrder.BandOf(restored, NOW));
    }

    [Fact]
    public void ANewChatNobodyAskedAnythingIsNeverFoldedAndComesFirstOfItsBand()
    {
        HexChatFacts[] chats = [Done(1, NOW - 10_000), Fresh(2), Done(3, NOW - 60 * 60_000)];
        List<HexChatPlace> order = Order(chats);

        Assert.Equal([2, 1, 3], Ids(chats, order));
        Assert.Equal(HexChatBand.Recent, order[0].Band);
    }

    [Fact]
    public void AFamilyStaysTogetherInItsMostPressingBand()
    {
        // A finished lead whose subagent works, the subagent's finished sibling under it too
        HexChatFacts[] chats =
        [
            Done(1, NOW - 60 * 60_000),
            Done(2, NOW - 30 * 60_000, parent: 1),
            Working(3, NOW - 1_000, parent: 1),
            Working(4, NOW - 20_000),
        ];
        List<HexChatPlace> order = Order(chats);

        Assert.Equal([1, 3, 4, 2], Ids(chats, order));
        Assert.Equal([0, 1, 0, 0], order.Select(place => place.Step));
        Assert.All(order.Take(2), place => Assert.Equal(HexChatBand.Working, place.Band));
        Assert.Equal(HexChatBand.Finished, order[3].Band);
    }

    [Fact]
    public void AFinishedSubagentOfAWorkingParentFoldsIntoTheFinished()
    {
        HexChatFacts[] chats = [Working(1, NOW - 1_000), Done(2, NOW - 60 * 60_000, parent: 1), Done(3, NOW - 10_000, parent: 1), Done(4, NOW - 90 * 60_000, parent: 2)];
        List<HexChatPlace> order = Order(chats);

        // The one just done stays under its parent a few minutes, the one done long ago folds with what it started
        Assert.Equal([1, 3, 2, 4], Ids(chats, order));
        Assert.Equal([HexChatBand.Working, HexChatBand.Working, HexChatBand.Finished, HexChatBand.Finished], order.Select(place => place.Band));
        Assert.Equal([0, 1, 0, 1], order.Select(place => place.Step));
    }

    [Fact]
    public void AFinishedSubagentOpenStaysUnderItsWorkingParent()
    {
        HexChatFacts[] chats = [Working(1, NOW - 1_000), Done(2, NOW - 60 * 60_000, parent: 1)];

        Assert.Equal([1, 2], Ids(chats, Order(chats, open: 2, lift: true)));
        Assert.DoesNotContain(Order(chats, open: 2, lift: true), place => place.Band == HexChatBand.Finished);
    }

    [Fact]
    public void ASubagentWaitingOnThePersonTakesItsFamilyToTheTop()
    {
        HexChatFacts[] chats = [Working(1, NOW - 1_000), Done(2, NOW - 60 * 60_000), Asking(3, NOW - 9_000, parent: 2)];

        Assert.Equal([2, 3, 1], Ids(chats, Order(chats)));
    }

    [Fact]
    public void TheChatOpenIsLiftedOutOfTheFinishedWhileTheyreFolded()
    {
        HexChatFacts[] chats = [Done(1, NOW - 60 * 60_000), Done(2, NOW - 90 * 60_000, parent: 1), Working(3, NOW - 1_000)];

        List<HexChatPlace> folded = Order(chats, open: 2, lift: true);
        Assert.All(folded, place => Assert.NotEqual(HexChatBand.Finished, place.Band));

        List<HexChatPlace> shown = Order(chats, open: 2, lift: false);
        Assert.Equal(HexChatBand.Finished, shown.Single(place => chats[place.Index].Id == 2).Band);
    }

    [Fact]
    public void AChatWhoseStarterIsGoneIsAFamilyOfItsOwn()
    {
        HexChatFacts[] chats = [Working(5, NOW - 1_000, parent: 99), Done(6, NOW - 1_000)];
        List<HexChatPlace> order = Order(chats);

        Assert.Equal([5, 6], Ids(chats, order));
        Assert.All(order, place => Assert.Equal(0, place.Step));
    }

    [Fact]
    public void ParentsSaidRoundInACircleStillListEveryChatOnce()
    {
        HexChatFacts[] chats = [Working(1, NOW - 1_000, parent: 2), Working(2, NOW - 2_000, parent: 1)];

        Assert.Equal(2, Order(chats).Select(place => place.Index).Distinct().Count());
    }

    [Fact]
    public void NoChatsNoPlaces()
    {
        Assert.Empty(Order([]));
    }

    [Fact]
    public void AChatRemembersWhenItsTurnEnded()
    {
        var chat = new HexAgentChat(1, "claude_code");
        chat.Ask("look round");
        long started = chat.TurnStarted;
        Assert.True(started > 0);
        Assert.Equal(0, chat.Ended);

        chat.Done();

        Assert.True(chat.Ended >= started);
        HexChatFacts facts = HexChatFacts.Of(chat, needsYou: false);
        Assert.Equal(HexChatBand.Recent, HexAgentListOrder.BandOf(facts, chat.Ended + 1_000));
        Assert.Equal(HexChatBand.Finished, HexAgentListOrder.BandOf(facts, chat.Ended + HexAgentListOrder.UNREAD_RECENT_MS + 1));
    }
}
