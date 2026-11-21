using System.Text.Json.Nodes;
using Hex.Core;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Fact]
    public async Task A_locked_file_is_never_a_delete_and_sync_resumes_when_it_unlocks()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "locked.txt", "original");
        Put(pair.B, "locked.txt", "original");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        string full = Path.Combine(pair.B.Work, "locked.txt");
        using (var locked = new FileStream(full, FileMode.Truncate, FileAccess.Write, FileShare.None))
        {
            locked.Write("edited during lock"u8);
            locked.Flush();
            await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerB.Flush("work"));
            Assert.Equal("original", Get(pair.A, "locked.txt"));
            Assert.Empty(sync.Conflicts);
        }

        await Settled(sync);
        Assert.Equal("edited during lock", Get(pair.A, "locked.txt"));
    }

    [Fact]
    public async Task A_failed_git_listing_does_not_report_deletes_from_its_partial_tree()
    {
        if (NoGit)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Repositories(pair);
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        string index = Path.Combine(pair.B.Work, ".git", "index");
        byte[] original = File.ReadAllBytes(index);
        File.WriteAllText(index, "invalid git index");
        File.Delete(Path.Combine(pair.B.Work, "readme.md"));
        try
        {
            await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerB.Flush("work"));
            Assert.Equal("one\n", Get(pair.A, "readme.md"));
            Assert.Empty(sync.Conflicts);
        }
        finally
        {
            File.WriteAllBytes(index, original);
            Put(pair.B, "readme.md", "one\n");
        }
        await Settled(sync);
    }

    [Fact]
    public async Task An_unreadable_directory_does_not_turn_omissions_into_deletes()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.B, "private/file.txt", "base");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        string directory = Path.Combine(pair.B.Work, "private");
        UnixFileMode permissions = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            try
            {
                _ = Directory.EnumerateFiles(directory).ToArray();
                return; // A privileged test process retains access.
            }
            catch (UnauthorizedAccessException)
            {
                await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerB.Flush("work"));
                Assert.Equal("base", Get(pair.A, "private/file.txt"));
            }
        }
        finally
        {
            File.SetUnixFileMode(directory, permissions);
        }
    }

    [Fact]
    public void Truncated_and_oversized_manifests_are_refused()
    {
        HexLinkTree.CheckCount(HexLinkTree.MOST_FILES);
        var entries = new JsonArray(new JsonNode?[HexLinkTree.MOST_FILES]);
        var said = new JsonObject { ["folder"] = "work", ["entries"] = entries };
        Assert.Equal(HexLinkErrors.TOO_BIG, Assert.Throws<HexLinkException>(() => HexLinkTree.Of(said)).Code);
        entries.Add((JsonNode?)null);
        said["complete"] = true;
        Assert.Equal(HexLinkErrors.TOO_BIG, Assert.Throws<HexLinkException>(() => HexLinkTree.Of(said)).Code);
    }

    [Fact]
    public async Task The_watch_replays_an_edit_between_snapshot_and_activation()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.B, "gap.txt", "old");
        var tree = new HexLinkTree(pair.B.Workspace.Folders[0]);
        var changed = new TaskCompletionSource<HexLinkTreeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = new HexLinkTreeWatch(tree, changes =>
        {
            foreach (HexLinkTreeChange change in changes)
            {
                if (change.Path == "work/gap.txt")
                    changed.TrySetResult(change);
            }
        }, TimeSpan.FromMilliseconds(25), TimeSpan.FromMinutes(5));
        HexLinkManifest snapshot = tree.Manifest();
        Put(pair.B, "gap.txt", "edited in the gap");
        watch.Seed(snapshot);

        HexLinkTreeChange observed = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HexLinkFiles.Hash("edited in the gap"u8), observed.Hash);
        Assert.False(observed.Deleted);
    }

    [Fact]
    public async Task A_dirty_editor_rejects_the_write_and_keeps_a_conflict_copy()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "dirty.txt", "last saved");
        Put(pair.B, "dirty.txt", "last saved");
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            ApplyLocal = (_, wire, _, _, _, _) => throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} is dirty",
                new JsonObject { ["editor_dirty"] = true })
        };
        await sync.Start();
        await Settled(sync);

        Put(pair.B, "dirty.txt", "incoming");
        await Settled(sync);
        HexLinkSyncConflict conflict = Assert.Single(sync.Conflicts);
        Assert.Equal("unsaved changes in the editor", conflict.Kind);
        Assert.Equal("last saved", Get(pair.A, "dirty.txt"));
        Assert.Equal("incoming", Get(pair.A, conflict.Copy!["work/".Length..]));
    }
}
