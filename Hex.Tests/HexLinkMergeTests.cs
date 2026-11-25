using System.Text;
using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkMergeTests
{
    [Fact]
    public async Task Disjoint_edits_merge_and_the_local_crlf_and_bom_survive()
    {
        if (HexLinkGit.Why() is not null)
            return;
        byte[] baseline = Bytes("a\nb\nc\nd\ne\n");
        byte[] local = [0xef, 0xbb, 0xbf, .. Bytes("A\r\nb\r\nc\r\nd\r\ne\r\n")];
        byte[] remote = Bytes("a\nb\nc\nd\nE\n");
        var merged = Assert.IsType<HexLinkMergeResult>(await HexLinkMerge.TryTextAsync(baseline, local, remote));
        Assert.True(merged.Clean);
        Assert.Equal("utf-8-bom", HexLinkFiles.Decode(merged.Bytes)!.Value.Encoding);
        Assert.Equal("A\r\nb\r\nc\r\nd\r\nE\r\n", HexLinkFiles.Decode(merged.Bytes)!.Value.Text);
    }

    [Fact]
    public void Merge_without_git_makes_one_conflict_per_place_with_gits_labels()
    {
        var merged = Assert.IsType<HexLinkMergeResult>(HexLinkMerge.InProcess("a\nb\nc\nd\ne\n", "A\nb\nc\nd\nours\n", "a\nb\nc\nd\ntheirs\n", "utf-8"));
        Assert.False(merged.Clean);
        Assert.Equal("A\nb\nc\nd\n<<<<<<< Current\nours\n||||||| Base\ne\n=======\ntheirs\n>>>>>>> Incoming\n", Encoding.UTF8.GetString(merged.Bytes));

        var clean = Assert.IsType<HexLinkMergeResult>(HexLinkMerge.InProcess("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n", "utf-8"));
        Assert.True(clean.Clean);
        Assert.Equal("A\nb\nC\n", Encoding.UTF8.GetString(clean.Bytes));
    }

    [Fact]
    public async Task Conflicting_hunk_has_three_sides_while_clean_edits_are_already_combined()
    {
        if (HexLinkGit.Why() is not null)
            return;
        byte[] baseline = Bytes("a\nb\nc\nd\ne\nf\ng\nh\ni\n");
        byte[] local = Bytes("A\nb\nc\nd\nours\nf\ng\nh\ni\n");
        byte[] remote = Bytes("a\nb\nc\nd\ntheirs\nf\ng\nh\nI\n");
        var merged = Assert.IsType<HexLinkMergeResult>(await HexLinkMerge.TryTextAsync(baseline, local, remote));
        Assert.False(merged.Clean);
        string text = Encoding.UTF8.GetString(merged.Bytes);
        Assert.StartsWith("A\n", text);
        Assert.EndsWith("I\n", text);
        Assert.Contains("<<<<<<< Current\nours\n||||||| Base\ne\n=======\ntheirs\n>>>>>>> Incoming\n", text);
        Assert.Null(await HexLinkMerge.TryTextAsync(baseline, [0, 1, 2], remote));
    }

    [Fact]
    public async Task Sync_merges_disjoint_edits_and_only_overlaps_wait_for_resolution()
    {
        if (HexLinkGit.Why() is not null)
            return;
        var edit = HexLinkRights.View | HexLinkRights.Edit;
        using var pair = await LinkedPair.Up(edit, edit);
        string left = pair.A.Work, right = pair.B.Work, state = Path.Combine(pair.A.Root, "merge-state");
        const string baseline = "a\nb\nc\nd\ne\nf\ng\nh\ni\n";
        foreach (string root in new[] { left, right })
        {
            File.WriteAllText(Path.Combine(root, "clean.txt"), baseline);
            File.WriteAllText(Path.Combine(root, "overlap.txt"), baseline);
        }

        HexLinkSync NewSync() => new(pair.PeerB, pair.A.Workspace.Folders[0], state);
        using (var first = NewSync())
        {
            await first.Start();
            await first.Flush().WaitAsync(TimeSpan.FromSeconds(15));
        }

        File.WriteAllText(Path.Combine(left, "clean.txt"), "A" + baseline[1..]);
        File.WriteAllText(Path.Combine(right, "clean.txt"), baseline[..^2] + "I\n");
        string ours = "A\nb\nc\nd\nours\nf\ng\nh\ni\n", theirs = "a\nb\nc\nd\ntheirs\nf\ng\nh\nI\n";
        File.WriteAllText(Path.Combine(left, "overlap.txt"), ours);
        File.WriteAllText(Path.Combine(right, "overlap.txt"), theirs);

        using var resumed = NewSync();
        await resumed.Start();
        await resumed.Flush().WaitAsync(TimeSpan.FromSeconds(20));
        string combined = "A\nb\nc\nd\ne\nf\ng\nh\nI\n";
        Assert.Equal(combined, File.ReadAllText(Path.Combine(left, "clean.txt")));
        Assert.Equal(combined, File.ReadAllText(Path.Combine(right, "clean.txt")));
        Assert.Equal(ours, File.ReadAllText(Path.Combine(left, "overlap.txt")));
        Assert.Equal(theirs, File.ReadAllText(Path.Combine(right, "overlap.txt")));
        Assert.Equal("work/overlap.txt", Assert.Single(resumed.Conflicts).Path);
        HexLinkMergePreview preview = Assert.IsType<HexLinkMergePreview>(resumed.MergePreview("work/overlap.txt"));
        Assert.Contains("<<<<<<< Current", Encoding.UTF8.GetString(preview.Markers));

        string resolution = "A\nb\nc\nd\nours and theirs\nf\ng\nh\nI\n";
        Assert.Null(await resumed.PublishMerge("work/overlap.txt", preview, resolution));
        Assert.Equal(resolution, File.ReadAllText(Path.Combine(left, "overlap.txt")));
        Assert.Equal(resolution, File.ReadAllText(Path.Combine(right, "overlap.txt")));
        Assert.Empty(resumed.Conflicts);
    }

    [Theory]
    [InlineData("hello from a\n", "\n")]
    [InlineData("hello from a\r\nsecond\r\n", "\r\n")]
    [InlineData("hello from a\nsecond\nthird\r\n", "\n")]
    [InlineData("hello from a\r\nsecond\r\nthird\n", "\r\n")]
    [InlineData("hello from a\nsecond\r\n", "\n")]
    public void A_resolved_merge_is_written_in_the_files_dominant_line_ends_even_when_they_are_mixed(string original, string expected)
    {
        // Use mine, use theirs and use both come out as text of any ends; the file's own ends are put back on them
        foreach (string resolved in new[] { "hello from a\n", "hello from b\r\n", "hello from a\nhello from b\n" })
        {
            byte[] made = Assert.IsType<byte[]>(HexLinkMerge.EncodeLike(resolved, Bytes(original)));
            Assert.Equal(resolved.ReplaceLineEndings(expected), HexLinkFiles.Decode(made)!.Value.Text);
        }
    }

    [Fact]
    public void Lines_that_come_unchanged_from_either_copy_keep_their_own_line_end_byte_for_byte()
    {
        byte[] mine = Bytes("line one\r\nsame\nend\r\n"), theirs = Bytes("line two\nsame\nend\r\n");

        // Use mine, use theirs, use both: written for this side
        Assert.Equal("line one\r\nsame\nend\r\n", Text(HexLinkMerge.EncodeKeeping("line one\nsame\nend\n", mine, theirs)));
        Assert.Equal("line two\nsame\nend\r\n", Text(HexLinkMerge.EncodeKeeping("line two\nsame\nend\n", mine, theirs)));
        Assert.Equal("line one\r\nline two\nsame\nend\r\n", Text(HexLinkMerge.EncodeKeeping("line one\nline two\nsame\nend\n", mine, theirs)));

        // and for the other side, which has its own ends for what it had
        Assert.Equal("line one\r\nsame\nend\r\n", Text(HexLinkMerge.EncodeKeeping("line one\nsame\nend\n", theirs, mine)));
        Assert.Equal("line two\nsame\nend\r\n", Text(HexLinkMerge.EncodeKeeping("line two\nsame\nend\n", theirs, mine)));

        // a line that is new or edited takes the side's dominant end, here CRLF in a file of mostly CRLF
        Assert.Equal("new\r\nsame\nend\r\n", Text(HexLinkMerge.EncodeKeeping("new\nsame\nend\n", Bytes("a\r\nsame\nend\r\n"), theirs)));

        // repeated lines take the ends of their twins in turn
        Assert.Equal("x\r\nx\nx\r\n", Text(HexLinkMerge.EncodeKeeping("x\nx\nx\n", Bytes("x\r\nx\nx\r\n"), theirs)));

        // no end on the last line stays none
        Assert.Equal("line one\r\nsame", Text(HexLinkMerge.EncodeKeeping("line one\nsame", mine, theirs)));

        static string Text(byte[]? bytes) => Encoding.UTF8.GetString(Assert.IsType<byte[]>(bytes));
    }

    [Fact]
    public void A_receiving_file_with_one_line_end_keeps_it_for_lines_that_came_from_the_other_copy()
    {
        byte[] lf = Bytes("a-A\nb\nc\n"), crlf = Bytes("a\r\nb\r\nc\r\n");

        // Resolved on the LF side to "a-A", which the CRLF side never had: written there, it is all CRLF, not "a-A\n" then CRLF
        Assert.Equal("a-A\r\nb\r\nc\r\n", Encoding.UTF8.GetString(Assert.IsType<byte[]>(HexLinkMerge.EncodeKeeping("a-A\nb\nc\n", crlf, lf))));
        Assert.Equal("a-A\nb\nc\n", Encoding.UTF8.GetString(Assert.IsType<byte[]>(HexLinkMerge.EncodeKeeping("a-A\nb\r\nc\r\n", lf, crlf))));
        Assert.True(HexLinkMerge.IsUniform("a\r\nb\r\n"));
        Assert.False(HexLinkMerge.IsUniform("a\r\nb\n"));
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_and_mixed_ends_keeps_both()
    {
        byte[] original = [0xef, 0xbb, 0xbf, .. Bytes("hello from a\nsecond\nthird\r\n")];
        byte[] made = Assert.IsType<byte[]>(HexLinkMerge.EncodeLike("hello from b\n", original));
        Assert.Equal("utf-8-bom", HexLinkFiles.Decode(made)!.Value.Encoding);
        Assert.Equal("hello from b\n", HexLinkFiles.Decode(made)!.Value.Text);
    }

    [Fact]
    public void Binary_bytes_still_cannot_be_written_as_text()
    {
        Assert.Null(HexLinkMerge.EncodeLike("text\n", [0, 1, 2, 0xff]));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
