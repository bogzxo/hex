using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

// Hex restarting into a build of itself, what it hands the new Hex and how it gets a running Hex's files out of the way
public sealed class HexDevLoopTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-dev-loop-" + Guid.NewGuid().ToString("N"));

    public HexDevLoopTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_chat_written_down_reads_back_as_it_was_with_its_session_and_rows()
    {
        var chat = new HexAgentChat(4, "claude_code") { Parent = 2, Depth = 1, Name = "scout", Session = "s-1", Model = "haiku", Cost = 0.25, ContextUsed = 1200, ContextLimit = 200_000 };
        chat.Options["model"] = "haiku";
        chat.Ask("find it");
        chat.Say("found it");
        chat.Tool("t1", "mcp__hex__hex_symbols", "{\"query\":\"X\"}");
        chat.Tool("t2", "Bash", "{}");
        chat.ToolDone("t1", true, "a.cs:1");
        chat.Done();

        HexAgentChat back = HexAgentChatStore.Read(System.Text.Json.Nodes.JsonNode.Parse(HexAgentChatStore.Write(chat).ToJsonString()))!;
        Assert.Equal((4, "claude_code", 2, 1, "scout", "s-1", "haiku"), (back.Id, back.Agent, back.Parent, back.Depth, back.Name, back.Session, back.Model));
        Assert.Equal(("find it", 0.25, 1200L, 200_000L), (back.Title, back.Cost, back.ContextUsed, back.ContextLimit));
        Assert.Equal("haiku", back.Options["model"]);
        Assert.Equal(chat.Entries.Select(entry => (entry.Kind, entry.Text, entry.State)), back.Entries.Select(entry => (entry.Kind, entry.Text, entry.State)));
        Assert.False(back.Running);
        Assert.False(back.Unread);
    }

    [Fact]
    public void A_file_in_use_is_moved_aside_for_a_build_and_put_back_when_it_fails()
    {
        string locked = Path.Combine(folder, "Hex.Dawn.dll"), free = Path.Combine(folder, "notes.txt");
        File.WriteAllText(locked, "old build");
        File.WriteAllText(free, "free");

        List<(string From, string Aside)> moved;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            moved = DawnScene.MoveLockedAside(folder);
            Assert.Equal(locked, Assert.Single(moved).From);
            Assert.False(File.Exists(locked));

            // A failed build wrote half of something in its place, putting back takes the old one again
            File.WriteAllText(locked, "half a build");
            DawnScene.PutBack(moved);
        }

        Assert.Equal("old build", File.ReadAllText(locked));
        Assert.Equal("free", File.ReadAllText(free));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.hexold"));
    }

    [Fact]
    public void What_an_earlier_Hex_moved_aside_is_cleaned_away()
    {
        File.WriteAllText(Path.Combine(folder, "Hex.dll.123.hexold"), "old");
        File.WriteAllText(Path.Combine(folder, "Hex.dll"), "new");
        DawnScene.CleanAside(folder);
        Assert.Equal(["Hex.dll"], Directory.EnumerateFiles(folder).Select(Path.GetFileName));
    }
}
