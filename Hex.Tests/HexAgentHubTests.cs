using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The hub of the agents' sessions on its own, without a window. Chats taken in and let go of, a family's root and
/// children, the waits, and the snapshot the tools read off the editor's thread made again only when something moved.
/// </summary>
public sealed class HexAgentHubTests
{
    [Fact]
    public void ChatsBecomeSessionsWithTheirFamily()
    {
        var hub = new HexAgentHub();
        var root = new HexAgentChat(1, "claude_code");
        var child = new HexAgentChat(2, "codex") { Parent = 1, Depth = 1 };
        var grandchild = new HexAgentChat(3, "claude_code") { Parent = 2, Depth = 2 };
        hub.Chats.Add(root);
        hub.Chats.Add(child);
        hub.Add(grandchild);

        Assert.Equal(3, hub.Chats.Count);
        Assert.Equal([root, child, grandchild], hub.Chats.ToArray());
        Assert.Equal(HexSessionKind.Panel, hub.ById(1)!.Kind);
        Assert.Equal(HexSessionKind.Spawned, hub.ById(2)!.Kind);
        Assert.Equal("codex", hub.ById(2)!.System);
        Assert.Same(root, hub.RootOf(grandchild));
        Assert.Equal([child], hub.Children(root).ToArray());
        Assert.Null(hub.ChatById(9));

        hub.Waiting[2] = 1;
        Assert.True(hub.IsWaiting(child));
        Assert.False(hub.IsWaiting(root));

        // Let go of, its waits go with it
        Assert.True(hub.Chats.Remove(child));
        Assert.False(hub.Chats.Remove(child));
        Assert.False(hub.IsWaiting(child));
        Assert.Equal(2, hub.Sessions.Count);
    }

    [Fact]
    public void AFamilyKeepsOneLogOfWhatItsAgentsSaid()
    {
        var hub = new HexAgentHub();
        var root = new HexAgentChat(1, "claude_code");
        var scout = new HexAgentChat(2, "codex") { Parent = 1, Depth = 1 };
        var other = new HexAgentChat(3, "claude_code");
        hub.Add(root);
        hub.Add(scout);
        hub.Add(other);

        HexAgentMail found = new(hub.NextMailSeq(), 2, "#2", "finding", "it's in StartMcp", false) { To = 1 };
        HexAgentMail told = new(hub.NextMailSeq(), 1, "#1", "instruction", "read it next", true) { To = 2, Re = found.Seq };
        HexAgentMail person = new(hub.NextMailSeq(), null, "the person watching", "instruction", "stop there", true) { To = 1 };
        hub.MailOf(root).Add(found, HexMailRoute.WokeWait);
        hub.MailOf(scout).Add(told, HexMailRoute.Steered);
        hub.MailOf(root).Add(person, HexMailRoute.Inbox);

        // The family's one log, whichever of them it's asked of, and another family's apart
        Assert.Same(hub.MailOf(root), hub.MailOf(scout));
        Assert.NotSame(hub.MailOf(root), hub.MailOf(other));
        Assert.Equal([found.Seq, told.Seq, person.Seq], hub.MailOf(scout).All.Select(entry => entry.Mail.Seq).ToArray());
        Assert.Equal(HexMailRoute.Steered, hub.MailOf(root).All[1].Route);
        Assert.Equal([found, told], hub.MailOf(root).Between(1, 2).ToArray());
        Assert.Equal([person], hub.MailOf(root).Between(HexMailLog.USER, 1).ToArray());
        Assert.Equal(found.Seq, told.Re);

        var flows = hub.MailOf(root).Flows(since: 0);
        Assert.Equal(3, flows.Length);
        Assert.Contains((HexMailLog.USER, 1, 1, person.Seq), flows);
        Assert.Single(hub.MailOf(root).Flows(since: told.Seq));
    }

    [Fact]
    public void AFullLogLetsProgressGoBeforeInstructions()
    {
        var log = new HexMailLog();
        log.Add(new HexAgentMail(1, 2, "#2", "instruction", "keep me", true) { To = 1 }, HexMailRoute.Inbox);
        for (int i = 2; i <= 2001; i++)
            log.Add(new HexAgentMail(i, 2, "#2", i == 2 ? "progress" : "finding", "step", false) { To = 1 }, HexMailRoute.Inbox);

        Assert.Equal(2000, log.All.Count);
        Assert.Equal(1, log.All[0].Mail.Seq);
        Assert.DoesNotContain(log.All, entry => entry.Mail.Kind == "progress");

        // No progress left, the oldest finding goes and the instruction stays
        log.Add(new HexAgentMail(2002, 2, "#2", "finding", "more", false) { To = 1 }, HexMailRoute.Inbox);
        Assert.Equal(1, log.All[0].Mail.Seq);
        Assert.Equal(4, log.All[1].Mail.Seq);
    }

    [Fact]
    public void EachKindOfLimitIsKeptAndTheAccountsAreSaidAcrossChats()
    {
        var one = new HexAgentChat(1, "claude_code");
        var two = new HexAgentChat(2, "claude_code");
        one.Limited(new HexAgentLimit("seven_day", HexAgentLimitState.Ok, 0.34, null, null));
        one.Limited(new HexAgentLimit("five_hour", HexAgentLimitState.Ok, 0.10, null, null));
        two.Limited(new HexAgentLimit("five_hour", HexAgentLimitState.Warning, 0.85, null, null));

        // A later kind doesn't hide the earlier, the latest said is still the one Limit says
        Assert.Equal(2, one.Limits.Count);
        Assert.Equal("five_hour", one.Limit!.Kind);

        // Across the chats, the session first and its most used, then the week
        HexAgentLimit[] across = HexAgentLimits.Across([one, two]);
        Assert.Equal(["five_hour", "seven_day"], across.Select(limit => limit.Kind).ToArray());
        Assert.Equal(0.85, across[0].Used);
        Assert.Equal("session limit", HexAgentLimits.KindName("five_hour"));
    }

    [Fact]
    public void TheSnapshotIsMadeAgainOnlyWhenASessionMoved()
    {
        var hub = new HexAgentHub();
        var chat = new HexAgentChat(1, "claude_code");
        hub.Chats.Add(chat);

        Assert.True(hub.Pump(100));
        HexHubSnapshot first = hub.Snapshot;
        Assert.Equal("new", first.Rows[1].State);

        // Nothing moved, the same snapshot
        Assert.False(hub.Pump(100));
        Assert.Same(first, hub.Snapshot);

        chat.Ask("find the server");
        chat.Say("It starts in StartMcp, and goes on a while after that.");
        chat.Done();
        Assert.True(hub.Pump(10));
        HexHubRow row = hub.Snapshot.Rows[1];
        Assert.Equal("done", row.State);
        Assert.True(row.Reply.Length <= 12, row.Reply);
        Assert.True(hub.Snapshot.Version > first.Version);

        // A session gone is a snapshot without it
        hub.Chats.Remove(chat);
        Assert.True(hub.Pump(100));
        Assert.Empty(hub.Snapshot.Rows);
    }
}
