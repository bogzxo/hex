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

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
