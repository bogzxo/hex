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
        var incomplete = new JsonObject { ["folder"] = "work", ["entries"] = new JsonArray(), ["complete"] = false };
        Assert.Throws<HexLinkException>(() => HexLinkTree.Of(incomplete));
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

    [Fact]
    public async Task The_latest_sync_state_wins_if_an_older_save_is_delayed()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int olderThread = 0;
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            BeforeStateWrite = _ =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref olderThread))
                {
                    blocked.Set();
                    release.Wait(TimeSpan.FromSeconds(15));
                }
            }
        };
        await sync.Start();
        await Settled(sync);

        Put(pair.A, "first.txt", "first");
        await Settled(sync);
        Task older = Task.Run(() =>
        {
            Volatile.Write(ref olderThread, Environment.CurrentManagedThreadId);
            sync.Save();
        });
        try
        {
            Assert.True(await Task.Run(() => blocked.Wait(TimeSpan.FromSeconds(10))));
            Put(pair.A, "second.txt", "second");
            Assert.True(await Until(() => File.ReadAllText(sync.StateFile).Contains("work/second.txt", StringComparison.Ordinal), 10_000));
        }
        finally
        {
            release.Set();
        }

        await older.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("work/first.txt", File.ReadAllText(sync.StateFile));
        Assert.Contains("work/second.txt", File.ReadAllText(sync.StateFile));
    }

    [Fact]
    public async Task Different_case_spellings_on_an_insensitive_link_are_skipped()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "Name.txt", "A");
        Put(pair.B, "name.txt", "B");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        Assert.Equal("A", Get(pair.A, "Name.txt"));
        Assert.Equal("B", Get(pair.B, "name.txt"));
        Assert.Contains(sync.Skipped, skip => skip.Path == "work/Name.txt" && skip.Why.Contains("case", StringComparison.Ordinal));
        Assert.Contains(sync.Skipped, skip => skip.Path == "work/name.txt" && skip.Why.Contains("case", StringComparison.Ordinal));
        Assert.Equal(0, sync.Sent + sync.Received);
    }

    [Fact]
    public async Task Case_only_file_renames_arrive_as_renames_in_both_directions()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "Name.txt", "same bytes");
        Put(pair.B, "Name.txt", "same bytes");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        await Settled(sync);

        File.Move(Path.Combine(pair.A.Work, "Name.txt"), Path.Combine(pair.A.Work, "name.txt"));
        await Settled(sync);
        Assert.Equal("name.txt", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(pair.B.Work))));
        Assert.Equal("same bytes", Get(pair.B, "name.txt"));
        Assert.Equal(1, sync.Sent);

        File.Move(Path.Combine(pair.B.Work, "name.txt"), Path.Combine(pair.B.Work, "NAME.txt"));
        await Settled(sync);
        Assert.Equal("NAME.txt", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(pair.A.Work))));
        Assert.Equal("same bytes", Get(pair.A, "NAME.txt"));
        Assert.Equal(1, sync.Received);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task A_file_stays_queued_past_six_retries_and_resumes_without_a_new_event()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.B, "held.txt", "incoming");
        int attempts = 0, release = 0;
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            ApplyLocal = (full, wire, expected, bytes, cancel, executable) =>
            {
                Interlocked.Increment(ref attempts);
                if (Volatile.Read(ref release) == 0)
                    throw new IOException("File locked by another program.");
                return HexLinkFiles.CompareAndReplace(full, wire, expected, bytes, cancel, executable);
            }
        };
        await sync.Start();

        Assert.True(await Until(() => Volatile.Read(ref attempts) > 6 && sync.Queued == 1, 15_000), Said(sync));
        Assert.Equal(1, sync.FilesTotal);
        Assert.Equal(0, sync.FilesDone);
        Assert.Equal("incoming".Length, sync.BytesTotal);
        Assert.Contains("locked", sync.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Get(pair.A, "held.txt"));

        Volatile.Write(ref release, 1);
        await Settled(sync);
        Assert.Equal("incoming", Get(pair.A, "held.txt"));
        Assert.Equal(0, sync.Queued);
        Assert.Equal(0, sync.FilesTotal);
        Assert.Equal(0, sync.BytesTotal);
    }

    [Fact]
    public async Task A_paged_watch_reads_one_complete_snapshot_while_receiving_changes()
    {
        var (sessionA, sessionB) = Sessions(() => EDIT, () => EDIT);
        string hash = HexLinkFiles.Hash("bytes"u8);
        var changed = new TaskCompletionSource<HexLinkTreeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionB.Requested = (method, parameters, _) =>
        {
            if (method != "sync/watch")
                return Task.FromResult<JsonNode?>(new JsonObject());
            int offset = HexLinkData.Int(parameters, "offset") ?? 0;
            Assert.InRange(offset, 0, 1);
            if (offset == 1)
            {
                Assert.Equal("snapshot", HexLinkData.String(parameters, "snapshot", 64));
                sessionB.Tell("sync/changed", new JsonObject
                {
                    ["folder"] = "work",
                    ["changes"] = new JsonArray(HexLinkTree.ChangeJson(new HexLinkTreeChange("work/changed", hash)))
                });
            }
            var manifest = new HexLinkManifest("work", null, [new HexLinkTreeFile($"work/{offset}", hash, 5)]);
            JsonObject page = HexLinkTree.Json(manifest);
            page["snapshot"] = "snapshot";
            page["offset"] = offset;
            page["total"] = 2;
            return Task.FromResult<JsonNode?>(page);
        };

        var peer = new HexLinkPeer(sessionA, DeviceA);
        sessionA.Told = peer.Told;
        sessionA.Start();
        sessionB.Start();
        try
        {
            sessionB.Tell("link/rights", new JsonObject { ["granted"] = new JsonArray("view") });
            HexLinkManifest manifest = await peer.WatchTree("work", changes => changed.TrySetResult(Assert.Single(changes)));
            Assert.True(manifest.Certified);
            Assert.Equal(new[] { "work/0", "work/1" }, manifest.Files.Select(file => file.Path));
            Assert.Equal("work/changed", (await changed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Path);
        }
        finally
        {
            sessionA.Close("done");
            sessionB.Close("done");
        }
    }

    [Fact]
    public async Task Editor_save_staging_files_are_never_listed_or_watched()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        string stage = $".hex-save-{Guid.NewGuid():N}.tmp";
        Put(pair.B, stage, "partly staged editor save");
        HexLinkManifest manifest = await pair.PeerB.Manifest("work");
        Assert.DoesNotContain(manifest.Files, file => file.Path == $"work/{stage}");
        var tree = new HexLinkTree(pair.B.Workspace.Folders[0]);
        Assert.Contains(stage, tree.Ignored([stage]));
    }
}
