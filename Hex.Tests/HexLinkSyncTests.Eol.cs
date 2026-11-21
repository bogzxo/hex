using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// The sync between a checkout with CRLF line ends (Windows with core.autocrlf) and one with LF (a Mac) of the same
/// files: nothing sent for files that only differ in line ends, what is sent written in the receiver's, binary and
/// mixed files exact, a change of only line ends kept where it was made, and real differences still conflicts.
/// </summary>
public sealed partial class HexLinkSyncTests
{
    private static readonly string[] TEXTS = ["src/one.cs", "src/two.cs", "readme.md", "docs/notes.txt", "build.props"];

    private static string Lines(string eol, params string[] lines) => string.Concat(lines.Select(line => line + eol));

    private static void PutBytes(LinkServices side, string relative, byte[] bytes)
    {
        string full = Path.Combine(side.Work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static byte[]? Bytes(LinkServices side, string relative)
    {
        try
        {
            string full = Path.Combine(side.Work, relative);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // A's tree with CRLF line ends, B's the same text with LF, as a Windows and a Mac checkout of one commit have
    private static void CrlfAgainstLf(LinkedPair pair)
    {
        foreach (string path in TEXTS)
        {
            Put(pair.A, path, Lines("\r\n", $"// {path}", "line two", "line three"));
            Put(pair.B, path, Lines("\n", $"// {path}", "line two", "line three"));
        }
    }

    [Fact]
    public async Task A_crlf_tree_against_an_lf_tree_of_the_same_text_is_in_sync_with_nothing_sent()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        CrlfAgainstLf(pair);
        Put(pair.A, "same.txt", "no line ends at all");
        Put(pair.B, "same.txt", "no line ends at all");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.Equal(0, sync.Sent);
        Assert.Equal(0, sync.Received);
        Assert.Empty(sync.Conflicts);
        Assert.Equal(TEXTS.Length, sync.EolOnly);
        Assert.Contains($"{TEXTS.Length} only differing in line ends", sync.Summary);
        foreach (string path in TEXTS)
        {
            Assert.Contains("\r\n", Get(pair.A, path));
            Assert.DoesNotContain("\r", Get(pair.B, path));
        }

        Assert.Empty(Directory.EnumerateFiles(pair.A.Work, "*.theirs-*", SearchOption.AllDirectories));

        // Started again, it's all agreed from the state file: nothing read, nothing counted again
        sync.Stop();
        using HexLinkSync again = SyncOf(pair);
        await again.Start();
        await Settled(again);
        Assert.Equal(0, again.Sent + again.Received + again.EolOnly);
    }

    [Fact]
    public async Task An_edit_arrives_in_the_receivers_line_ends_both_ways_without_echoes()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        CrlfAgainstLf(pair);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Put(pair.B, "src/one.cs", Lines("\n", "changed on the Mac", "second"));
        Assert.True(await Until(() => Get(pair.A, "src/one.cs") == Lines("\r\n", "changed on the Mac", "second")), Said(sync));
        Assert.Equal(Lines("\n", "changed on the Mac", "second"), Get(pair.B, "src/one.cs"));

        Put(pair.A, "src/two.cs", Lines("\r\n", "changed on the PC", "second"));
        Assert.True(await Until(() => Get(pair.B, "src/two.cs") == Lines("\n", "changed on the PC", "second")), Said(sync));
        Assert.Equal(Lines("\r\n", "changed on the PC", "second"), Get(pair.A, "src/two.cs"));

        await Settled(sync);
        await Settled(sync);
        Assert.Equal(1, sync.Sent);
        Assert.Equal(1, sync.Received);
        Assert.Empty(sync.Conflicts);

        // And again on the same files, from their new bases
        Put(pair.A, "src/one.cs", Lines("\r\n", "back on the PC"));
        Assert.True(await Until(() => Get(pair.B, "src/one.cs") == Lines("\n", "back on the PC")), Said(sync));
        await Settled(sync);
        Assert.Equal(2, sync.Sent);
        Assert.Equal(1, sync.Received);
    }

    [Fact]
    public async Task Two_engines_across_line_ends_converge_without_ping_pong()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        CrlfAgainstLf(pair);
        using HexLinkSync onA = SyncOf(pair);
        using HexLinkSync onB = SyncOfB(pair);
        await Task.WhenAll(onA.Start(), onB.Start());
        await Task.WhenAll(Settled(onA), Settled(onB));
        Assert.Equal(0, onA.Sent + onA.Received + onB.Sent + onB.Received);

        Put(pair.B, "readme.md", Lines("\n", "from the Mac"));
        Put(pair.A, "docs/notes.txt", Lines("\r\n", "from the PC"));
        Assert.True(await Until(() => Get(pair.A, "readme.md") == Lines("\r\n", "from the Mac") && Get(pair.B, "docs/notes.txt") == Lines("\n", "from the PC")),
            Said(onA) + " / " + Said(onB));
        await Task.WhenAll(Settled(onA), Settled(onB));
        int moved = onA.Sent + onA.Received + onB.Sent + onB.Received;
        Assert.InRange(moved, 2, 4);

        await Task.Delay(1500);
        await Task.WhenAll(Settled(onA), Settled(onB));
        Assert.Equal(moved, onA.Sent + onA.Received + onB.Sent + onB.Received);
        Assert.Equal(Lines("\r\n", "from the Mac"), Get(pair.A, "readme.md"));
        Assert.Equal(Lines("\n", "from the PC"), Get(pair.B, "docs/notes.txt"));
        Assert.Empty(onA.Conflicts);
        Assert.Empty(onB.Conflicts);
    }

    [Fact]
    public async Task A_new_file_takes_the_line_ends_most_files_have_on_the_receiving_side()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        CrlfAgainstLf(pair);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Put(pair.B, "new/from-mac.cs", Lines("\n", "made", "on the Mac"));
        Assert.True(await Until(() => Get(pair.A, "new/from-mac.cs") == Lines("\r\n", "made", "on the Mac")), Said(sync));
        Put(pair.A, "new/from-pc.cs", Lines("\r\n", "made", "on the PC"));
        Assert.True(await Until(() => Get(pair.B, "new/from-pc.cs") == Lines("\n", "made", "on the PC")), Said(sync));
        await Settled(sync);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task Binary_and_mixed_files_travel_byte_for_byte()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        CrlfAgainstLf(pair);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x0A, 0xFF, 0x0D, 0x0A];
        byte[] notUtf8 = [0x41, 0x0A, 0xC3, 0x28, 0x0A, 0x42];
        byte[] mixed = Encoding.UTF8.GetBytes("a\r\nb\nc\r\n");
        byte[] loneCr = Encoding.UTF8.GetBytes("old\rmac\r");
        PutBytes(pair.B, "image.png", binary);
        PutBytes(pair.B, "latin.txt", notUtf8);
        PutBytes(pair.B, "mixed.txt", mixed);
        PutBytes(pair.A, "classic.txt", loneCr);
        Assert.True(await Until(() => Bytes(pair.A, "image.png") is { } a && a.SequenceEqual(binary)
            && Bytes(pair.A, "latin.txt") is { } l && l.SequenceEqual(notUtf8)
            && Bytes(pair.A, "mixed.txt") is { } m && m.SequenceEqual(mixed)
            && Bytes(pair.B, "classic.txt") is { } c && c.SequenceEqual(loneCr)), Said(sync));
        await Settled(sync);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task A_change_of_only_line_ends_on_one_side_stays_there()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "flip.txt", Lines("\n", "one", "two"));
        Put(pair.B, "flip.txt", Lines("\n", "one", "two"));
        Put(pair.A, "flop.txt", Lines("\n", "three"));
        Put(pair.B, "flop.txt", Lines("\n", "three"));
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Put(pair.A, "flip.txt", Lines("\r\n", "one", "two"));
        Put(pair.B, "flop.txt", Lines("\r\n", "three"));
        Assert.True(await Until(() => sync.EolOnly == 2), Said(sync));
        await Settled(sync);
        Assert.Equal(Lines("\n", "one", "two"), Get(pair.B, "flip.txt"));
        Assert.Equal(Lines("\n", "three"), Get(pair.A, "flop.txt"));
        Assert.Equal(0, sync.Sent + sync.Received);
        Assert.Empty(sync.Conflicts);

        // A real edit after the flip goes, in the other side's line ends
        Put(pair.A, "flip.txt", Lines("\r\n", "one", "two", "three"));
        Assert.True(await Until(() => Get(pair.B, "flip.txt") == Lines("\n", "one", "two", "three")), Said(sync));
    }

    [Fact]
    public async Task Text_that_really_differs_is_still_a_conflict_whatever_its_line_ends()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "differs.txt", Lines("\r\n", "one", "two"));
        Put(pair.B, "differs.txt", Lines("\n", "one", "THREE"));
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.True(await Until(() => sync.Conflicts.Count == 1), Said(sync));
        await Settled(sync);
        Assert.Equal(Lines("\r\n", "one", "two"), Get(pair.A, "differs.txt"));
        Assert.Equal(Lines("\n", "one", "THREE"), Get(pair.B, "differs.txt"));
        Assert.Equal(Lines("\n", "one", "THREE"), Get(pair.A, "differs.theirs-laptop.txt"));
        Assert.Equal(0, sync.EolOnly);

        // Kept mine: it goes in the other side's line ends
        Assert.Null(await sync.Resolve("work/differs.txt", HexLinkSyncKeep.Mine));
        Assert.Equal(Lines("\n", "one", "two"), Get(pair.B, "differs.txt"));
    }

    [Fact]
    public async Task A_state_file_from_before_line_ends_were_kept_is_still_read()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "kept.txt", "as agreed");
        Put(pair.B, "kept.txt", "changed there since");
        string folder = Path.Combine(pair.A.Root, "state");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, HexLinkSync.StateName(DeviceB.Id, "work"));
        File.WriteAllText(file, new JsonObject
        {
            ["version"] = 1,
            ["device"] = DeviceB.Id,
            ["folder"] = "work",
            ["bases"] = new JsonObject { ["work/kept.txt"] = HexLinkFiles.Hash(Encoding.UTF8.GetBytes("as agreed")) },
            ["conflicts"] = new JsonArray()
        }.ToJsonString());

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        // With the old base this side is still at it, so the change there comes here rather than conflicting
        Assert.True(await Until(() => Get(pair.A, "kept.txt") == "changed there since"), Said(sync));
        await Settled(sync);
        Assert.Empty(sync.Conflicts);
        sync.Stop();
        JsonObject saved = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(2, (int)saved["version"]!);
        Assert.Equal(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("changed there since")), (string?)saved["bases"]!["work/kept.txt"]!["remote"]);
    }

    [Fact]
    public void Text_is_the_same_whatever_its_line_ends_and_binary_never_is()
    {
        Assert.True(HexLinkSync.SameText(Encoding.UTF8.GetBytes("a\r\nb\r\n"), Encoding.UTF8.GetBytes("a\nb\n")));
        Assert.True(HexLinkSync.SameText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\r\n")], [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\n")]));
        Assert.False(HexLinkSync.SameText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\n")], Encoding.UTF8.GetBytes("a\n")));
        Assert.False(HexLinkSync.SameText(Encoding.UTF8.GetBytes("a\r\nb"), Encoding.UTF8.GetBytes("a\nc")));
        Assert.False(HexLinkSync.SameText([0, 13, 10], [0, 10]));
        Assert.True(HexLinkSync.SameText([0, 13, 10], [0, 13, 10]));
    }
}
