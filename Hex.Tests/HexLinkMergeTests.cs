using System.Text;
using Hex.Core;

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

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
