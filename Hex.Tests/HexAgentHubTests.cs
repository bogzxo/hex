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
