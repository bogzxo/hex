using System.Text;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    private static async Task<HexLinkPeer> PackedPeer(LinkedPair pair)
    {
        var (a, b) = Pair();
        await Task.WhenAll(
            pair.LinkA.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => pair.BOnA, bulk: HexLinkBulkSupport.Raw)),
            pair.LinkB.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => pair.AOnB, bulk: HexLinkBulkSupport.Raw)))
            .WaitAsync(TimeSpan.FromMilliseconds(LONG_MS));
        return pair.LinkA.PeerOf(DeviceB.Id)!;
    }

    [Fact]
    public async Task A_file_pack_reports_each_checked_write_and_keeps_the_loser_of_a_race()
    {
        using var pair = new LinkedPair(EDIT, EDIT);
        HexLinkPeer peer = await PackedPeer(pair);
        Put(pair.B, "changed.txt", "someone saved a newer file");
        const string path = "work/changed.txt";
        byte[] proposed = Encoding.UTF8.GetBytes("from the peer");
        string old = HexLinkFiles.Hash(Encoding.UTF8.GetBytes("old hash"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(LONG_MS));

        var result = await peer.WriteFilePack("work",
            [new HexLinkFilePack.Entry(path, proposed, old), new HexLinkFilePack.Entry("work/new.txt", proposed)], cancel.Token);

        Assert.Equal(HexLinkErrors.CONFLICT, result[path].Error?.Code);
        Assert.Equal(HexLinkFiles.Hash(proposed), result["work/new.txt"].Hash);
        Assert.Equal("someone saved a newer file", Get(pair.B, "changed.txt"));
        Assert.Equal("from the peer", Get(pair.B, "new.txt"));
    }

    [Fact]
    public async Task Binary_working_tree_sync_batches_small_files_and_reads_them_back()
    {
        using var pair = new LinkedPair(EDIT, EDIT);
        for (int i = 0; i < 64; i++)
        {
            Put(pair.A, $"from-a/{i}.txt", $"A {i}");
            Put(pair.B, $"from-b/{i}.txt", $"B {i}");
        }

        HexLinkPeer peer = await PackedPeer(pair);
        using var sync = SyncOf(pair, peer);
        var totals = new System.Collections.Concurrent.ConcurrentBag<int>();
        sync.Changed = () => totals.Add(sync.FilesTotal);
        await sync.Start();
        await Settled(sync);

        for (int i = 0; i < 64; i++)
        {
            Assert.Equal($"A {i}", Get(pair.B, $"from-a/{i}.txt"));
            Assert.Equal($"B {i}", Get(pair.A, $"from-b/{i}.txt"));
        }

        Assert.Contains(128, totals);
        Assert.Equal(sync.FilesTotal, sync.FilesDone);
        Assert.Equal(64, sync.Sent);
        Assert.Equal(64, sync.Received);
        Assert.Contains(pair.B.Audited, entry => entry.Detail.Contains("sync/pack/write", StringComparison.Ordinal));
        Assert.DoesNotContain(pair.B.Audited, entry => entry.Detail.StartsWith("fs/write", StringComparison.Ordinal));
    }
}
