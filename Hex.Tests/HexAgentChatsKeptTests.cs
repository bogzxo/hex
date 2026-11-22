using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// A workspace's chats kept on disk and read back, as a Hex that closed or went down finds them on its next start. Only
/// the chats that moved are written again, the ones closed go, and a workspace's folder is the same for its first folder
/// however it's written.
/// </summary>
public sealed class HexAgentChatsKeptTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "hex-kept-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void ChatsComeBackInOrderWithTheOneThatWasOpen()
    {
        string folder = Temp();
        try
        {
            var first = new HexAgentChat(3, "claude_code") { Session = "s-1", Model = "claude-opus-5-5" };
            first.Ask("find the server");
            first.Say("It starts in StartMcp.");
            first.Done();
            var second = new HexAgentChat(5, "codex") { Parent = 3, Depth = 1, Name = "Scout" };
            second.Ask("look round");

            var kept = new HexAgentChatsKept(folder);
            kept.Keep([first, second], new JsonObject { ["open"] = 5, ["made"] = 7 });
            kept.Flush();

            var (chats, state) = new HexAgentChatsKept(folder).Read();
            Assert.Equal([3, 5], chats.Select(chat => chat.Id).ToArray());
            Assert.Equal("s-1", chats[0].Session);
            Assert.Equal("It starts in StartMcp.", chats[0].Entries.Last().Text);
            Assert.Equal(3, chats[1].Parent);
            Assert.Equal("Scout", chats[1].Name);
            Assert.False(chats[1].Running);
            Assert.Equal(5, state!["open"]!.GetValue<int>());
            Assert.Equal(7, state["made"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DetachedBranchKeepsItsSessionsAndPersistsNewDepths()
    {
        string folder = Temp();
        try
        {
            var lead = new HexAgentChat(1, "codex");
            var branch = new HexAgentChat(2, "codex") { Parent = lead.Id, Depth = 1, Session = "branch-session" };
            var child = new HexAgentChat(3, "codex") { Parent = branch.Id, Depth = 2 };
            var leaf = new HexAgentChat(4, "codex") { Parent = child.Id, Depth = 3 };
            var sibling = new HexAgentChat(5, "codex") { Parent = lead.Id, Depth = 1 };
            branch.Ask("Active work");
            child.Ask("Some child work");
            child.Done();
            child.Seen = child.Version;

            HexAgentChat[] chats = [lead, branch, child, leaf, sibling];
            var hub = new HexAgentHub();
            foreach (HexAgentChat chat in chats)
                hub.Add(chat);
            HexAgentSession? session = hub.ById(branch.Id);
            var branchMail = new HexAgentMail(hub.NextMailSeq(), child.Id, "Child", "finding", "Child result", false) { To = branch.Id };
            var otherMail = new HexAgentMail(hub.NextMailSeq(), lead.Id, "Lead", "finding", "Sibling result", false) { To = sibling.Id };
            branch.Post(branchMail);
            HexMailLog original = hub.MailOf(lead);
            original.Add(branchMail, HexMailRoute.Inbox);
            original.Add(otherMail, HexMailRoute.Inbox);
            var kept = new HexAgentChatsKept(folder);
            kept.Keep(chats, new JsonObject());
            kept.Flush();
            hub.Pump(100);

            Assert.True(hub.Detach(branch));
            Assert.False(hub.Detach(branch));
            Assert.Same(session, hub.ById(branch.Id));
            Assert.Same(branch, hub.RootOf(leaf));
            Assert.Equal([sibling], hub.Children(lead));
            Assert.True(branch.Running);
            Assert.False(child.Unread);
            Assert.Equal(branchMail, Assert.Single(branch.UnreadMail()));
            Assert.Equal(branchMail, Assert.Single(hub.MailOf(branch).All).Mail);
            Assert.Equal(2, original.All.Count);
            Assert.True(hub.Pump(100));
            Assert.Null(hub.Snapshot.Rows[branch.Id].Parent);
            Assert.Equal(2, hub.Snapshot.Rows[leaf.Id].Depth);

            kept.Keep(chats, new JsonObject { ["open"] = branch.Id });
            kept.Flush();
            var (restored, state) = new HexAgentChatsKept(folder).Read();
            Assert.Equal([(1, (int?)null, 0), (2, null, 0), (3, 2, 1), (4, 3, 2), (5, 1, 1)],
                restored.Select(chat => (chat.Id, chat.Parent, chat.Depth)));
            HexAgentChat restoredBranch = restored[1];
            Assert.Equal(branch.Key, restoredBranch.Key);
            Assert.Equal("branch-session", restoredBranch.Session);
            Assert.Equal(branch.Id, state!["open"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void OnlyChatsThatMovedAreWrittenAndClosedOnesGo()
    {
        string folder = Temp();
        try
        {
            var kept = new HexAgentChatsKept(folder);
            var still = new HexAgentChat(1, "claude_code");
            still.Ask("stay");
            var moving = new HexAgentChat(2, "claude_code");
            moving.Ask("move");
            kept.Keep([still, moving], new JsonObject());
            kept.Flush();

            string stillFile = Path.Combine(folder, $"chat-{still.Key}.json");
            DateTime stillAt = File.GetLastWriteTimeUtc(stillFile);
            File.SetLastWriteTimeUtc(stillFile, stillAt.AddMinutes(-5));
            moving.Say("moved");
            kept.Keep([still, moving], new JsonObject());
            kept.Flush();
            Assert.Equal(stillAt.AddMinutes(-5), File.GetLastWriteTimeUtc(stillFile));
            Assert.Equal("moved", new HexAgentChatsKept(folder).Read().Chats[1].Entries.Last().Text);

            kept.Keep([moving], new JsonObject());
            kept.Flush();
            Assert.False(File.Exists(stillFile));
            Assert.Single(new HexAgentChatsKept(folder).Read().Chats);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FilesNothingNamesAreTidiedAsTheyreReadBack()
    {
        string folder = Temp();
        try
        {
            var chat = new HexAgentChat(1, "claude_code");
            chat.Ask("hello");
            var keeping = new HexAgentChatsKept(folder);
            keeping.Keep([chat], new JsonObject());
            keeping.Flush();
            string stray = Path.Combine(folder, "chat-gone.json");
            File.WriteAllText(stray, "{}");
            Directory.CreateDirectory(Path.Combine(folder, "history"));
            string strayRows = Path.Combine(folder, "history", "gone.jsonl");
            File.WriteAllText(strayRows, "{}\n");
            string ownRows = Path.Combine(folder, "history", chat.Key + ".jsonl");
            File.WriteAllText(ownRows, "{}\n");

            Assert.Single(new HexAgentChatsKept(folder).Read().Chats);
            Assert.False(File.Exists(stray));
            Assert.False(File.Exists(strayRows));
            Assert.True(File.Exists(ownRows));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AWorkspacesFolderGoesByItsFirstFolder()
    {
        string root = Path.GetTempPath();
        string project = Path.Combine(root, "Some Project");
        Assert.Equal(HexAgentChatsKept.FolderFor(root, project), HexAgentChatsKept.FolderFor(root, project + Path.DirectorySeparatorChar));
        Assert.NotEqual(HexAgentChatsKept.FolderFor(root, project), HexAgentChatsKept.FolderFor(root, Path.Combine(root, "Another")));
        Assert.StartsWith("SomeProject-", Path.GetFileName(HexAgentChatsKept.FolderFor(root, project)));
        Assert.Equal(Path.Combine(root, "none"), HexAgentChatsKept.FolderFor(root, null));
    }

    [Fact]
    public void PastTheMostTheOldestIdleChatsGo()
    {
        var chats = Enumerable.Range(1, HexAgentChatsKept.MOST_CHATS + 3).Select(id => new HexAgentChat(id, "claude_code")).ToList();
        // The oldest started another that's kept, so it stays with it
        chats.Add(new HexAgentChat(100, "claude_code") { Parent = 1, Depth = 1 });
        List<HexAgentChat> over = HexAgentChatsKept.PastMost(chats);
        Assert.Equal([2, 3, 4, 5], over.Select(chat => chat.Id).ToArray());
    }
}
