using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkTalkCodeTests
{
    private static HexLinkTalkRef Code(string snippet = "int x = 1;") =>
        new(HexLinkTalkRef.CODE, "hex/Hex.Core/Link/HexLinkTalk.cs", 10, 12, HexLinkTalkRef.CapSnippet(snippet), "Look at this");

    [Fact]
    public void A_message_with_a_reference_round_trips_and_an_old_message_still_reads()
    {
        var message = new HexLinkTalk(Guid.NewGuid(), DeviceB, Code().Fallback(), DateTimeOffset.UtcNow, Ref: Code());
        HexLinkTalk? read = HexLinkTalk.Of(message.ToJson(), DeviceB);
        Assert.NotNull(read);
        Assert.Equal(message.Ref, read.Ref);
        Assert.Contains("hex/Hex.Core/Link/HexLinkTalk.cs:10-12", read.Text);

        // The old format has no ref and reads as before, and a new message with its ref taken off still shows its text
        var old = new HexLinkTalk(Guid.NewGuid(), DeviceB, "plain", DateTimeOffset.UtcNow);
        Assert.Null(old.ToJson()["ref"]);
        Assert.Null(HexLinkTalk.Of(old.ToJson(), DeviceB)!.Ref);
        JsonObject stripped = message.ToJson();
        stripped.Remove("ref");
        Assert.Equal(message.Text, HexLinkTalk.Of(stripped, DeviceB)!.Text);

        // A ref that is no good is dropped, the message stays
        JsonObject bad = message.ToJson();
        bad["ref"] = new JsonObject { ["kind"] = "nonsense", ["path"] = "a.cs", ["line"] = 1 };
        Assert.Null(HexLinkTalk.Of(bad, DeviceB)!.Ref);
    }

    [Fact]
    public void A_look_here_carries_its_target_and_a_snippet_is_capped()
    {
        var look = new HexLinkTalkRef(HexLinkTalkRef.LOOK, "hex/a.cs", 5, 5, To: DeviceA.Id);
        Assert.Equal(look, HexLinkTalkRef.Of(look.ToJson()));
        Assert.Equal("hex/a.cs:5 (look here)", look.Fallback());

        string many = string.Join('\n', Enumerable.Range(0, 200).Select(i => "line " + i));
        string capped = HexLinkTalkRef.CapSnippet(many);
        Assert.Equal(HexLinkTalkRef.MOST_LINES, capped.Split('\n').Length);

        string wide = HexLinkTalkRef.CapSnippet(new string('x', 10_000) + "\r\n\u0007tail");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(wide) <= HexLinkTalkRef.MOST_SNIPPET_BYTES);
        Assert.DoesNotContain('\r', HexLinkTalkRef.CapSnippet("a\r\nb\u0007c"));
        Assert.Equal("a\nb c", HexLinkTalkRef.CapSnippet("a\r\nb\u0007c"));

        Assert.True(Code(many).Fallback().Length <= HexLinkTalk.MOST_TEXT);
        Assert.True(HexLinkTalk.Valid(Code(many).Fallback()));
    }

    [Fact]
    public void File_references_in_text_are_found_with_either_slash()
    {
        var found = HexLinkTalkText.References(@"see src/app/main.cs:123 and Hex.Core\Link\HexLinkTalk.cs:12-20, also util.py:7. not http://host.com:8080 or C:\x");
        Assert.Equal(["src/app/main.cs", @"Hex.Core\Link\HexLinkTalk.cs", "util.py"], found.Select(each => each.Path));
        Assert.Equal([(123, 123), (12, 20), (7, 7)], found.Select(each => (each.Line, each.ToLine)));
        Assert.Empty(HexLinkTalkText.References("version 1.2:3 and nothing"));
    }

    [Fact]
    public void Mentions_find_a_device_by_name()
    {
        Assert.Equal(["My Laptop"], HexLinkTalkText.Mentions("hey @my laptop, look", ["My Laptop", "Desk"]));
        Assert.Empty(HexLinkTalkText.Mentions("mail a@Desk.com", ["Desk"]));
        Assert.Equal(["Desk"], HexLinkTalkText.Mentions("@Desk", ["Desk"]));
        Assert.Equal((4, "De"), HexLinkTalkText.Completing("hey @De", 7));
        Assert.Null(HexLinkTalkText.Completing("hey there", 9));
    }

    [Fact]
    public void The_history_keeps_the_last_500_and_reads_back()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-talk-history-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "talk-history.json");
        try
        {
            List<HexLinkTalk> all = [.. Enumerable.Range(0, 520).Select(i =>
                new HexLinkTalk(Guid.NewGuid(), i % 2 == 0 ? DeviceA : DeviceB, $"message {i}", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + i),
                    Ref: i == 519 ? Code() : null))];
            HexLinkTalkHistory.Save(file, all);
            List<HexLinkTalk> back = HexLinkTalkHistory.Load(file);
            Assert.Equal(500, back.Count);
            Assert.Equal("message 20", back[0].Text);
            Assert.Equal(all[^1].Id, back[^1].Id);
            Assert.Equal(all[^1].Ref, back[^1].Ref);
            Assert.Equal((all[^1].Sender.Id, all[^1].Sender.Name), (back[^1].Sender.Id, back[^1].Sender.Name));
            Assert.NotEqual(back[^1].Sender.Id, back[^2].Sender.Id);

            File.WriteAllText(file, "not json");
            Assert.Empty(HexLinkTalkHistory.Load(file));
            Assert.Empty(HexLinkTalkHistory.Load(Path.Combine(folder, "missing.json")));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
