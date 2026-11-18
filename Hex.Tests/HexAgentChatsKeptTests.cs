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

            new HexAgentChatsKept(folder).Keep([first, second], new JsonObject { ["open"] = 5, ["made"] = 7 });

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

            string stillFile = Path.Combine(folder, $"chat-{still.Key}.json");
            DateTime stillAt = File.GetLastWriteTimeUtc(stillFile);
            File.SetLastWriteTimeUtc(stillFile, stillAt.AddMinutes(-5));
            moving.Say("moved");
            kept.Keep([still, moving], new JsonObject());
            Assert.Equal(stillAt.AddMinutes(-5), File.GetLastWriteTimeUtc(stillFile));
            Assert.Equal("moved", new HexAgentChatsKept(folder).Read().Chats[1].Entries.Last().Text);

            kept.Keep([moving], new JsonObject());
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
            new HexAgentChatsKept(folder).Keep([chat], new JsonObject());
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
