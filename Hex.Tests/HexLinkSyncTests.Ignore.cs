using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Fact]
    public async Task Link_ignore_matches_gitignore_patterns_in_a_plain_folder()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, HexLinkIgnore.FileName,
            "# Link rules\n\n/at-root\nobj/\n*.tmp\n!important.tmp\ncache/**/trace\nhidden/\n!hidden/keep\n" +
            "\\#literal\n\\!literal\nfile[0-9].gen\nspace\\ \n{x,y}\n.hex-link-ignore\n");
        string[] ignored = ["at-root", "a/obj/build", "a/drop.tmp", "cache/trace", "cache/a/b/trace",
            "hidden/keep", "#literal", "!literal", "file3.gen", "space ", "{x,y}"];
        string[] visible = ["nested/at-root", "obj", "important.tmp", "nested/important.tmp", "nested/cache/trace",
            "cache/a/b/other", "filex.gen", "x", HexLinkIgnore.FileName];

        var tree = new HexLinkTree(pair.A.Workspace.Folders[0]);
        Assert.Equal(ignored.Order(), tree.Ignored([.. ignored, .. visible]).Order());
    }

    [Fact]
    public async Task Link_ignore_skips_both_directions_and_reloads_while_sync_is_running()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, HexLinkIgnore.FileName, "*.generated\n");
        Put(pair.A, "only-a.generated", "A");
        Put(pair.B, "only-b.generated", "B");
        Put(pair.A, "visible.txt", "shared");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Equal("*.generated\n", Get(pair.B, HexLinkIgnore.FileName));
        Assert.Equal("shared", Get(pair.B, "visible.txt"));
        Assert.False(Has(pair.B, "only-a.generated"));
        Assert.False(Has(pair.A, "only-b.generated"));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED,
            await CodeOf(pair.PeerA.Write("work/incoming.generated", null, "incoming"u8.ToArray())));
        Assert.False(Has(pair.A, "incoming.generated"));

        Put(pair.A, "live-a.generated", "new A");
        Put(pair.B, "live-b.generated", "new B");
        await Settled(sync);
        Assert.False(Has(pair.B, "live-a.generated"));
        Assert.False(Has(pair.A, "live-b.generated"));

        Put(pair.A, HexLinkIgnore.FileName, "# Everything can travel now.\n");
        await Settled(sync);
        await Settled(sync);
        Assert.Equal("A", Get(pair.B, "only-a.generated"));
        Assert.Equal("B", Get(pair.A, "only-b.generated"));
        Assert.Equal("new A", Get(pair.B, "live-a.generated"));
        Assert.Equal("new B", Get(pair.A, "live-b.generated"));
        Assert.Empty(sync.Conflicts);

        Put(pair.A, HexLinkIgnore.FileName, "*.generated\n");
        await Settled(sync);
        File.Delete(Path.Combine(pair.A.Work, "only-a.generated"));
        await Settled(sync);
        Assert.Equal("A", Get(pair.B, "only-a.generated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worktree_safety_a_new_remote_ignore_rule_never_turns_a_missing_file_into_a_delete(bool restart)
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        const string path = "tracked.txt";
        Put(pair.A, path, "agreed");
        Put(pair.B, path, "agreed");
        using var sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        if (restart)
            sync.Stop();
        Put(pair.B, HexLinkIgnore.FileName, path + "\n");
        File.Delete(Path.Combine(pair.B.Work, path));
        if (restart)
        {
            using var resumed = SyncOf(pair);
            await resumed.Start();
            await Settled(resumed);
            Assert.Empty(resumed.Conflicts);
        }
        else
        {
            await Settled(sync);
            Assert.Empty(sync.Conflicts);
        }
        Assert.Equal("agreed", Get(pair.A, path));
        Assert.False(Has(pair.B, path));
    }

    [Fact]
    public async Task Link_ignore_rules_apply_to_tracked_files_and_the_rules_file_always_travels()
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair, ".hex-link-ignore\n");
        Put(pair.A, HexLinkIgnore.FileName, "readme.md\n");
        Put(pair.A, "readme.md", "private change here\n");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Equal("readme.md\n", Get(pair.B, HexLinkIgnore.FileName));
        Assert.Equal("one\n", Get(pair.B, "readme.md"));
        Assert.Empty(sync.Conflicts);
    }
}
