using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_locked_at_start_retries_without_blocking_other_files(bool local)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        LinkServices lockedSide = local ? pair.A : pair.B;
        LinkServices receiving = local ? pair.B : pair.A;
        Put(lockedSide, "locked.txt", "locked on first scan");
        Put(lockedSide, "healthy.txt", "healthy");
        using HexLinkSync sync = SyncOf(pair);
        using (new FileStream(Path.Combine(lockedSide.Work, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sync.Start();
            Assert.True(await Until(() => Get(receiving, "healthy.txt") == "healthy"), Said(sync));
            Assert.True(await Until(() => sync.Queued > 0 && sync.Skipped.Any(file => file.Path == "work/locked.txt")), Said(sync));
            Assert.False(Has(receiving, "locked.txt"));
            Assert.Empty(sync.Conflicts);
        }
        await Settled(sync);
        Assert.Equal("locked on first scan", Get(receiving, "locked.txt"));
        Assert.Equal(0, sync.Queued);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_initial_one_sided_tracked_edit_uses_HEAD_across_CRLF_and_LF(bool localDirty)
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        LinkServices dirty = localDirty ? pair.A : pair.B;
        LinkServices clean = localDirty ? pair.B : pair.A;
        Put(dirty, "readme.md", "changed\n");
        Put(clean, "readme.md", "one\r\n");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Equal("changed\n", Get(dirty, "readme.md"));
        Assert.Equal("changed\r\n", Get(clean, "readme.md"));
        Assert.Empty(sync.Conflicts);
        Assert.Empty(Directory.EnumerateFiles(pair.A.Work, "*.theirs-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_initial_tracked_deletion_propagates_only_over_unmodified_HEAD(bool localDeleted)
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        LinkServices deleted = localDeleted ? pair.A : pair.B;
        LinkServices clean = localDeleted ? pair.B : pair.A;
        File.Delete(Path.Combine(deleted.Work, "readme.md"));
        Put(clean, "readme.md", "one\r\n");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.False(Has(deleted, "readme.md"));
        Assert.False(Has(clean, "readme.md"));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task An_initial_comparison_merges_disjoint_tracked_changes_and_keeps_only_overlaps_as_conflicts()
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        Put(pair.A, "disjoint.txt", "one\ntwo\nthree\nfour\n");
        Git(pair.A.Work, "add", "disjoint.txt");
        Git(pair.A.Work, "commit", "-qm", "Tracked base");
        Git(pair.B.Work, "fetch", "-q", pair.A.Work, "main");
        Git(pair.B.Work, "merge", "-q", "--ff-only", "FETCH_HEAD");

        Put(pair.A, "disjoint.txt", "desk\ntwo\nthree\nfour\n");
        Put(pair.B, "disjoint.txt", "one\ntwo\nthree\nlaptop\n");
        Put(pair.A, "readme.md", "desk\n");
        Put(pair.B, "readme.md", "laptop\n");
        Put(pair.A, "new.txt", "untracked");
        Put(pair.A, "collision.txt", "untracked A");
        Put(pair.B, "collision.txt", "untracked B");

        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);
        Assert.Equal("desk\ntwo\nthree\nlaptop\n", Get(pair.A, "disjoint.txt"));
        Assert.Equal("desk\ntwo\nthree\nlaptop\n", Get(pair.B, "disjoint.txt"));
        Assert.Equal("untracked", Get(pair.B, "new.txt"));
        Assert.Equal("desk\n", Get(pair.A, "readme.md"));
        Assert.Equal("laptop\n", Get(pair.B, "readme.md"));
        Assert.Equal("untracked A", Get(pair.A, "collision.txt"));
        Assert.Equal("untracked B", Get(pair.B, "collision.txt"));
        Assert.Equal(2, sync.Conflicts.Count);
        Assert.Contains(sync.Conflicts, conflict => conflict.Path == "work/collision.txt");
        HexLinkSyncConflict overlap = Assert.Single(sync.Conflicts, conflict => conflict.Path == "work/readme.md");
        Assert.Contains("<<<<<<<", Get(pair.A, overlap.Copy!["work/".Length..]));
    }

    [Fact]
    public async Task An_initial_comparison_uses_each_nested_repository_and_submodule_HEAD()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair, "/nested/\n");
        string nested = Path.Combine(pair.A.Work, "nested"), module = Path.Combine(pair.A.Root, "module-source");
        foreach (string repo in new[] { nested, module })
        {
            Directory.CreateDirectory(repo);
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "user.name", "Test");
            Git(repo, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(repo, "tracked.txt"), "clean\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-qm", "First");
        }
        Git(nested, "-c", "protocol.file.allow=always", "submodule", "add", "-q", module, "lib/module");
        Git(nested, "commit", "-qam", "Module");
        Git(pair.B.Work, "-c", "protocol.file.allow=always", "clone", "-q", "--recursive", "-c", "core.autocrlf=false", nested, "nested");

        Put(pair.A, "nested/tracked.txt", "changed nested\n");
        Put(pair.B, "nested/lib/module/tracked.txt", "changed module\n");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Equal("changed nested\n", Get(pair.B, "nested/tracked.txt")?.ReplaceLineEndings("\n"));
        Assert.Equal("changed module\n", Get(pair.A, "nested/lib/module/tracked.txt")?.ReplaceLineEndings("\n"));
        Assert.Empty(sync.Conflicts);
    }
}
