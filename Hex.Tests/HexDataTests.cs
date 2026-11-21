using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>The data folder: where it is, the old files moved into it, and what tidying it takes away and keeps.</summary>
[Collection(HexHome.NAME)]
public sealed class HexDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-data-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private string Write(string relative, string text = "x", DateTime? written = null)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (written is { } at)
            File.SetLastWriteTimeUtc(path, at);
        return path;
    }

    [Fact]
    public void TheFolderIsNextToTheExeWhenItCanBeWritten()
    {
        var (folder, fallback) = HexData.Choose(Path.Combine(root, "bin"), Path.Combine(root, "local"), _ => true);
        Assert.Equal(Path.Combine(root, "bin", "data"), folder);
        Assert.False(fallback);
    }

    [Fact]
    public void AFolderThatCantBeWrittenFallsBackToLocalAppDataLaidOutTheSame()
    {
        var (folder, fallback) = HexData.Choose(Path.Combine(root, "Program Files", "Hex"), Path.Combine(root, "local"), _ => false);
        Assert.True(fallback);
        Assert.StartsWith(Path.Combine(root, "local"), folder);
        Assert.Equal("data", Path.GetFileName(folder));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void TheLayoutIsUnderTheRoot()
    {
        string data = HexData.Root;
        foreach (string place in (string[])[HexData.AgentChats, HexData.AgentHistory, HexData.AgentImages, HexData.AgentTemp, HexData.AgentLimits, HexData.HarnessCache])
            Assert.StartsWith(data, place);
        Assert.Equal(Path.Combine(data, "agents", "chats"), HexData.AgentChats);
        Assert.Equal(Path.Combine(data, "cache", "harness"), HexData.HarnessCache);
    }

    [Fact]
    public void OldFilesMoveOverAndConflictsAreLeftWhereTheyWere()
    {
        string settings = Path.Combine(root, "settings"), data = Path.Combine(root, "data");
        Write("settings/agent_chats/ws-1/chat-a.json", "old a");
        Write("settings/agent_chats/ws-1/history/a.jsonl", "rows");
        Write("settings/agent_chats/ws-1/chat-b.json", "old b");
        Write("data/agents/chats/ws-1/chat-b.json", "new b");
        Write("settings/agent_limits.tsv", "limits");
        Write("settings/settings.hor", "mine");
        (string, string)[] places =
        [
            (Path.Combine(settings, "agent_chats"), Path.Combine(data, "agents", "chats")),
            (Path.Combine(settings, "agent_limits.tsv"), Path.Combine(data, "agents", "limits.tsv")),
            (Path.Combine(settings, "agent_images"), Path.Combine(data, "agents", "images"))
        ];
        string log = Path.Combine(data, "moved.log");

        HexData.Moved moved = HexData.MoveOld(places, log);

        Assert.Equal(new HexData.Moved(3, 1, 0), moved);
        Assert.Equal("old a", File.ReadAllText(Path.Combine(data, "agents", "chats", "ws-1", "chat-a.json")));
        Assert.Equal("rows", File.ReadAllText(Path.Combine(data, "agents", "chats", "ws-1", "history", "a.jsonl")));
        Assert.Equal("limits", File.ReadAllText(Path.Combine(data, "agents", "limits.tsv")));
        // The one already there wasn't written over, and the old one wasn't deleted
        Assert.Equal("new b", File.ReadAllText(Path.Combine(data, "agents", "chats", "ws-1", "chat-b.json")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(settings, "agent_chats", "ws-1", "chat-b.json")));
        // Emptied folders went, the folder with what didn't move stayed, the settings were left alone
        Assert.False(Directory.Exists(Path.Combine(settings, "agent_chats", "ws-1", "history")));
        Assert.True(File.Exists(Path.Combine(settings, "settings.hor")));
        Assert.Contains("skipped", File.ReadAllText(log));

        // Again, nothing more moves and nothing is lost
        Assert.Equal(new HexData.Moved(0, 1, 0), HexData.MoveOld(places, log));
    }

    [Fact]
    public void NothingToMoveWritesNoLog()
    {
        string log = Path.Combine(root, "data", "moved.log");
        Assert.Equal(default, HexData.MoveOld([(Path.Combine(root, "none"), Path.Combine(root, "data", "x"))], log));
        Assert.False(File.Exists(log));
    }

    [Fact]
    public void OldChatsAreOtherWorkspacesAndOnlyTheClosableOfTheOpenOne()
    {
        DateTime old = DateTime.UtcNow.AddDays(-40), recent = DateTime.UtcNow.AddDays(-1);
        string chats = Path.Combine(root, "chats");
        Write("chats/other/chat-o1.json", "{}", old);
        Write("chats/other/history/o1.jsonl", "rows", old);
        Write("chats/other/chat-o2.json", "{}", recent);
        Write("chats/open/chat-p1.json", "{}", old);
        Write("chats/open/chat-p2.json", "{}", old);

        List<HexOldChat> found = HexDataTidy.OldChats(chats, DateTime.UtcNow.AddDays(-30), Path.Combine(chats, "open"), new HashSet<string> { "p2" });

        Assert.Equal(["o1", "p2"], found.Select(chat => chat.Key).Order().ToArray());
        Assert.Equal(6, found.Single(chat => chat.Key == "o1").Size);
    }

    [Fact]
    public void DeletedChatsGoWithTheirRowsAndOutOfTheList()
    {
        DateTime old = DateTime.UtcNow.AddDays(-40);
        string chats = Path.Combine(root, "chats");
        Write("chats/ws/chat-a.json", "{}", old);
        Write("chats/ws/history/a.jsonl", "rows", old);
        Write("chats/ws/chat-b.json", "{}");
        Write("chats/ws/chats.json", new JsonObject { ["open"] = 2, ["chats"] = new JsonArray("a", "b") }.ToJsonString());
        Write("chats/gone/chat-c.json", "{}", old);
        Write("chats/gone/chats.json", new JsonObject { ["chats"] = new JsonArray("c") }.ToJsonString());

        int deleted = HexDataTidy.DeleteChats(HexDataTidy.OldChats(chats, DateTime.UtcNow.AddDays(-30), null, new HashSet<string>()));

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(Path.Combine(chats, "ws", "chat-a.json")));
        Assert.False(File.Exists(Path.Combine(chats, "ws", "history", "a.jsonl")));
        Assert.True(File.Exists(Path.Combine(chats, "ws", "chat-b.json")));
        var list = JsonNode.Parse(File.ReadAllText(Path.Combine(chats, "ws", "chats.json")))!;
        Assert.Equal(["b"], list["chats"]!.AsArray().Select(key => key!.GetValue<string>()).ToArray());
        Assert.Equal(2, list["open"]!.GetValue<int>());
        // A workspace with no chats left goes altogether
        Assert.False(Directory.Exists(Path.Combine(chats, "gone")));
    }

    [Fact]
    public void UnusedPicturesAreThoseNoChatMentionsNorTheBoxHolds()
    {
        DateTime old = DateTime.UtcNow.AddHours(-2);
        string images = Path.Combine(root, "images");
        Write("images/pasted-20261101-101010-1.png", "png", old);
        string mentioned = Write("images/pasted-20261101-101010-2.png", "png", old);
        string pending = Write("images/pasted-20261101-101010-3.png", "png", old);
        Write("images/pasted-20261101-101010-4.png", "png");
        Write("chats/ws/chat-a.json", "{\"text\":\"look at C:\\\\data\\\\agents\\\\images\\\\pasted-20261101-101010-2.png\"}");

        List<string> unused = HexDataTidy.UnusedImages(images, [Path.Combine(root, "chats")], [pending], DateTime.UtcNow);

        Assert.Equal(["pasted-20261101-101010-1.png"], unused.Select(file => Path.GetFileName(file)).ToArray());
        Assert.True(File.Exists(mentioned));
    }

    [Fact]
    public void TemporaryFilesJustWrittenAreKept()
    {
        Write("temp/claude-mcp-3.json", "{}", DateTime.UtcNow.AddHours(-3));
        Write("temp/claude-mcp-4.json", "{}");
        Write("old-temp/claude-settings-1.json", "{}", DateTime.UtcNow.AddDays(-2));

        List<string> old = HexDataTidy.OldTemp([Path.Combine(root, "temp"), Path.Combine(root, "old-temp"), Path.Combine(root, "missing")], DateTime.UtcNow);

        Assert.Equal(["claude-mcp-3.json", "claude-settings-1.json"], old.Select(file => Path.GetFileName(file)).Order().ToArray());
        Assert.Equal(2, HexDataTidy.DeleteFiles(old));
        Assert.True(File.Exists(Path.Combine(root, "temp", "claude-mcp-4.json")));
    }

    [Fact]
    public void UsageAddsUpEachPart()
    {
        Write("chats/ws/chat-a.json", "12345");
        Write("history/b.jsonl", "123");
        Write("images/pasted-20261101-101010-1.png", "12", DateTime.UtcNow.AddHours(-1));
        Write("temp/f.json", "1");
        Write("cache/harness/index.bin", "1234");

        HexDataUsage usage = HexDataTidy.Measure(Path.Combine(root, "chats"), Path.Combine(root, "history"), Path.Combine(root, "images"),
            [Path.Combine(root, "temp")], Path.Combine(root, "cache"), DateTime.UtcNow.AddHours(1));

        Assert.Equal((8L, 2L, 1L, 4L, 15L), (usage.Chats, usage.Images, usage.Temp, usage.Cache, usage.Total));
        Assert.Equal(1, usage.ChatCount);
        Assert.Equal((1, 1L), usage.TempFiles);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void SizesReadAsPeopleSayThem(long bytes, string said) => Assert.Equal(said, HexDataTidy.Bytes(bytes));
}
