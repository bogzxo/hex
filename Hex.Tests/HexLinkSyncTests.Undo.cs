using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>Undo after a conflict is settled (docs/hex-link-plan.md, 5.5): the losing version is kept and can come back.</summary>
public sealed partial class HexLinkSyncTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Both sides changed work/f.txt since they agreed on it: A holds "mine", B "theirs", the conflict is waiting
    private static async Task<HexLinkSync> InConflict(LinkedPair pair)
    {
        Put(pair.A, "f.txt", "base");
        Put(pair.B, "f.txt", "base");
        HexLinkSync first = SyncOf(pair);
        await first.Start();
        await Settled(first);
        first.Stop();
        first.Dispose();

        Put(pair.A, "f.txt", "mine");
        Put(pair.B, "f.txt", "theirs");
        HexLinkSync sync = SyncOf(pair);
        await sync.Start();
        Assert.True(await Until(() => sync.Conflicts.Count == 1), Said(sync));
        await Settled(sync);
        return sync;
    }

    [Fact]
    public async Task Undo_after_keep_mine_brings_the_other_version_back_on_both_nodes()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = await InConflict(pair);

        Assert.Null(await sync.Resolve("work/f.txt", HexLinkSyncKeep.Mine));
        Assert.True(await Until(() => Get(pair.B, "f.txt") == "mine"), Said(sync));
        HexLinkResolution record = Assert.Single(sync.Resolutions());
        Assert.Equal("work/f.txt", record.Path);
        Assert.Equal(HexLinkSyncKeep.Mine, record.Keep);

        HexLinkUndoResult undone = await sync.Undo(record.Id);
        Assert.Equal(HexLinkUndoStatus.Undone, undone.Status);
        Assert.Equal("theirs", Get(pair.A, "f.txt"));
        Assert.True(await Until(() => Get(pair.B, "f.txt") == "theirs"), Said(sync));
        await Settled(sync);
        Assert.Equal("theirs", Get(pair.A, "f.txt"));
        Assert.Empty(sync.Resolutions());
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task Undo_after_use_theirs_brings_this_sides_version_back_on_both_nodes()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = await InConflict(pair);

        Assert.Null(await sync.Resolve("work/f.txt", HexLinkSyncKeep.Theirs));
        Assert.Equal("theirs", Get(pair.A, "f.txt"));
        // The sync has seen its own write by the time somebody presses Undo
        await Settled(sync);

        HexLinkResolution record = Assert.Single(sync.Resolutions());
        Assert.Equal(HexLinkUndoStatus.Undone, (await sync.Undo(record.Id)).Status);
        Assert.Equal("mine", Get(pair.A, "f.txt"));
        Assert.True(await Until(() => Get(pair.B, "f.txt") == "mine"), Said(sync));
        await Settled(sync);
        Assert.Equal(HexLinkUndoStatus.Refused, (await sync.Undo(record.Id)).Status);
    }

    [Fact]
    public async Task Undo_after_a_further_edit_writes_nothing_and_offers_the_merge()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync sync = await InConflict(pair);

        Assert.Null(await sync.Resolve("work/f.txt", HexLinkSyncKeep.Mine));
        HexLinkResolution record = Assert.Single(sync.Resolutions());
        Put(pair.A, "f.txt", "mine, edited since");

        HexLinkUndoResult result = await sync.Undo(record.Id);
        Assert.Equal(HexLinkUndoStatus.NeedsMerge, result.Status);
        Assert.Equal("needs_merge", result.Word);
        Assert.Equal("mine, edited since", Get(pair.A, "f.txt"));
        Assert.Single(sync.Resolutions());
        Assert.NotNull(await sync.UndoMergeSession(record.Id, CancellationToken.None));
    }

    [Fact]
    public void Resolutions_expire_after_seven_days_and_only_the_last_fifty_are_kept()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-resolutions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var store = new HexLinkResolutions(folder, clock);
            byte[] one = "one"u8.ToArray(), two = "two"u8.ToArray();
            HexLinkResolution old = store.Add("work/old.txt", "B", "id", HexLinkSyncKeep.Mine, one, two, HexLinkFiles.Hash(one))!;
            Assert.Equal(two, store.Bytes(old, old.LoserHash));

            clock.Now = clock.Now.AddDays(6);
            Assert.Single(store.List());
            clock.Now = clock.Now.AddDays(2);
            Assert.Empty(store.List());
            Assert.Null(store.Get(old.Id));
            Assert.False(Directory.Exists(Path.Combine(folder, old.Id)));

            for (int index = 0; index < 55; index++)
            {
                clock.Now = clock.Now.AddMinutes(1);
                store.Add($"work/{index}.txt", "B", "id", HexLinkSyncKeep.Theirs, one, two, null);
            }

            IReadOnlyList<HexLinkResolution> kept = store.List();
            Assert.Equal(HexLinkResolutions.MOST, kept.Count);
            Assert.Equal("work/54.txt", kept[0].Path);
            Assert.Equal(50, Directory.GetDirectories(folder).Length);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
