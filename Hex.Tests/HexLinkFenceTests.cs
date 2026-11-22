using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkFenceTests
{
    private static string Scope() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Waiting_repo_writer_drains_overlapping_live_work_and_leaves_siblings_free()
    {
        string root = Scope();
        var repo = HexLinkPublicationFence.ForRoot(Path.Combine(root, "repo"));
        var sibling = HexLinkPublicationFence.ForRoot(Path.Combine(root, "repo-other"));
        var nested = HexLinkPublicationFence.ForRoot(Path.Combine(root, "repo", "src", "test.cs"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var live = await nested.EnterLive(stop.Token);
        var waiting = repo.EnterExclusive(HexLinkFenceKind.GitFollow, stop.Token).AsTask();
        var later = nested.EnterLive(stop.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.False(later.IsCompleted);
        Assert.Equal(new HexLinkFenceStatus(HexLinkFenceKind.GitFollow, false), repo.Status);
        await using var elsewhere = await sibling.EnterExclusive(HexLinkFenceKind.BulkApply, stop.Token);
        await live.DisposeAsync();
        var exclusive = await waiting;
        Assert.False(later.IsCompleted);
        await exclusive.DisposeAsync();
        await using var released = await later;
        Assert.Null(repo.Status);
    }

    [Fact]
    public async Task Expiration_cancels_the_writer_but_retains_exclusion_until_it_unwinds()
    {
        string root = Scope();
        var folder = HexLinkPublicationFence.ForRoot(root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lease = await folder.EnterExclusive(HexLinkFenceKind.BulkApply, stop.Token, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, lease.Stop));
        var next = folder.EnterLive(stop.Token).AsTask();
        Assert.False(next.IsCompleted);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await using var released = await next;
    }

    [Fact]
    public async Task Interrupted_set_only_allows_its_own_recovery()
    {
        string root = Scope();
        var folder = HexLinkPublicationFence.ForRoot(root);
        var repo = HexLinkPublicationFence.ForRoot(Path.Combine(root, "nested"));
        Guid id = Guid.NewGuid();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        folder.HoldRecovery(id, HexLinkFenceKind.Publication);
        try
        {
            var other = repo.EnterExclusive(HexLinkFenceKind.GitFollow, stop.Token, TimeSpan.FromMilliseconds(50)).AsTask();
            await Assert.ThrowsAsync<HexLinkException>(() => other);
            Assert.Equal(new HexLinkFenceStatus(HexLinkFenceKind.Publication, true), repo.Status);
            var live = repo.EnterLive(stop.Token).AsTask();
            var recovery = await folder.EnterExclusive(HexLinkFenceKind.Publication, stop.Token, recovery: id);
            Assert.False(live.IsCompleted);
            folder.ReleaseRecovery(id);
            Assert.False(live.IsCompleted);
            await recovery.DisposeAsync();
            await using var released = await live;
            Assert.Null(repo.Status);
        }
        finally
        {
            folder.ReleaseRecovery(id);
        }
    }

    [Fact]
    public async Task Remote_receipt_survives_lost_ack_and_clears_only_its_peer()
    {
        string root = Scope();
        try
        {
            var set = Set();
            var source = new HexLinkCommittedSets(Path.Combine(root, "a"));
            var target = new HexLinkCommittedSets(Path.Combine(root, "b"));
            source.Queue(set, ["b", "c"]);
            await Assert.ThrowsAsync<IOException>(() => source.Deliver("b", (batch, _) =>
            {
                Assert.Null(target.PrepareIncoming(batch));
                target.CompleteIncoming(batch);
                throw new IOException("Disconnected after commit.");
            }, default));

            source = new HexLinkCommittedSets(Path.Combine(root, "a"));
            target = new HexLinkCommittedSets(Path.Combine(root, "b"));
            Assert.Single(source.Pending("b"));
            Assert.Empty(target.UnfinishedIncoming());
            await source.Deliver("b", (batch, _) => Task.FromResult(target.PrepareIncoming(batch)!.Value), default);
            Assert.Empty(source.Pending("b"));
            Assert.Single(source.Pending("c"));
            Assert.Equal("c", Assert.Single(source.Queue(set, ["new-peer"])));
            Assert.Empty(source.Pending("new-peer"));
            Assert.Throws<HexLinkException>(() => target.PrepareIncoming(set with { Epoch = 2 }));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Interrupted_replica_and_its_exact_hashes_survive_reopening()
    {
        string root = Scope();
        try
        {
            var set = Set();
            var ledger = new HexLinkCommittedSets(root);
            Assert.Null(ledger.PrepareIncoming(set));
            ledger = new HexLinkCommittedSets(root);
            Assert.Equal(set.Files[0], Assert.Single(Assert.Single(ledger.UnfinishedIncoming()).Files));
            var receipt = ledger.CompleteIncoming(set);
            Assert.Equal(receipt, ledger.PrepareIncoming(set));
            Assert.Empty(ledger.UnfinishedIncoming());
            Assert.Throws<HexLinkException>(() => ledger.PrepareIncoming(set with { Scope = "outside" }));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static HexLinkCommittedSet Set() =>
        new(Guid.NewGuid(), "root", "repo", 1, HexLinkFenceKind.Publication,
            [new HexLinkCommittedFile("root/repo/file.txt", new string('a', 64), new string('b', 64))]);
}
