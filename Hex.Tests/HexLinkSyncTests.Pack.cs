using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{

    [Theory]
    [InlineData(HexLinkFilePack.MaximumFile + 1024)]
    [InlineData(HexLinkFiles.MOST_READ + 1024)]
    public async Task Binary_sync_copies_medium_and_streamed_files_in_each_direction(int length)
    {
        using var pair = new LinkedPair(EDIT, EDIT);
        byte[] fromA = new byte[length], fromB = new byte[length];
        new Random(21).NextBytes(fromA);
        new Random(22).NextBytes(fromB);
        File.WriteAllBytes(Path.Combine(pair.A.Work, "large-a.bin"), fromA);
        File.WriteAllBytes(Path.Combine(pair.B.Work, "large-b.bin"), fromB);

        HexLinkPeer peer = await PackedPeer(pair);
        using var sync = SyncOf(pair, peer);
        await sync.Start();
        await Settled(sync);

        Assert.Equal(HexLinkFiles.Hash(fromA), HashLarge(Path.Combine(pair.B.Work, "large-a.bin")));
        Assert.Equal(HexLinkFiles.Hash(fromB), HashLarge(Path.Combine(pair.A.Work, "large-b.bin")));
        if (length <= HexLinkFiles.MOST_READ)
            Assert.Contains(pair.B.Audited, entry => entry.Detail.StartsWith("sync/pack/write", StringComparison.Ordinal));
        Assert.Empty(sync.Skipped);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task An_interrupted_stream_resumes_from_a_checked_prefix_on_the_next_link()
    {
        using var pair = new LinkedPair(EDIT, EDIT);
        HexLinkPeer first = await PackedPeer(pair);
        byte[] bytes = new byte[HexLinkFiles.MOST_READ + 1024];
        new Random(23).NextBytes(bytes);
        string source = Path.Combine(pair.A.Work, "resume.bin");
        File.WriteAllBytes(source, bytes);
        var transfer = new HexLinkFileTransfer("work/resume.bin", bytes.Length, HexLinkFiles.Hash(bytes));
        const int prefix = 1024 * 1024;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(LONG_MS));

        var metadata = transfer.Json();
        new HexLinkFileTransfer.Checkpoint(0, null).PutIn(metadata);
        await using (HexLinkSession.BulkUpload upload = await first.Session.OpenBulkAsync("sync/file/write", bytes.Length, metadata, timeout.Token))
        {
            await upload.WriteAsync(bytes.AsMemory(0, prefix), timeout.Token);
            while (true)
            {
                var reply = await first.Session.Ask("sync/file/resume", transfer.Json(), timeout.Token);
                if (HexLinkFileTransfer.Checkpoint.Of(reply!.AsObject(), bytes.Length).Offset == prefix)
                    break;
                await Task.Delay(40, timeout.Token);
            }

            first.Session.Close("test interruption");
        }

        Assert.True(await Until(() => pair.LinkA.PeerOf(DeviceB.Id) is null && pair.LinkB.PeerOf(DeviceA.Id) is null));
        HexLinkPeer resumed = await PackedPeer(pair);
        var checkpoint = (await resumed.Session.Ask("sync/file/resume", transfer.Json(), timeout.Token))!.AsObject();
        Assert.Equal(new HexLinkFileTransfer.Checkpoint(prefix, HexLinkFiles.Hash(bytes.AsSpan(0, prefix))),
            HexLinkFileTransfer.Checkpoint.Of(checkpoint, bytes.Length));
        await resumed.WriteStreamedFile(transfer, source, timeout.Token);
        Assert.Equal(transfer.Hash, HashLarge(Path.Combine(pair.B.Work, "resume.bin")));
        var completed = (await resumed.Session.Ask("sync/file/resume", transfer.Json(), timeout.Token))!.AsObject();
        Assert.Equal(0, HexLinkFileTransfer.Checkpoint.Of(completed, bytes.Length).Offset);
    }


    [Fact]
    public async Task Equal_directory_digests_leave_only_changed_entries_on_the_wire()
    {
        using var pair = new LinkedPair(EDIT, EDIT);
        Put(pair.A, "same/one.txt", "equal");
        Put(pair.B, "same/one.txt", "equal");
        Put(pair.A, "changed/two.txt", "previous");
        Put(pair.B, "changed/two.txt", "next");
        HexLinkPeer peer = await PackedPeer(pair);
        HexLinkManifest basis = new HexLinkTree(pair.A.Workspace.Folders[0]).Manifest();
        HexLinkDirectoryDigests digests = HexLinkDirectoryDigests.From(basis);
        var offer = digests.Offer();

        var said = (await peer.Session.Ask("sync/watch", new JsonObject
        {
            ["folder"] = basis.Folder,
            ["paged"] = true,
            ["directories"] = offer.DeepClone()
        }))!.AsObject();
        Assert.Equal("work/same", Assert.Single(said["equal_directories"]!.AsArray())!.GetValue<string>());
        HexLinkManifest changed = HexLinkTree.Of(said);
        Assert.Equal("work/changed/two.txt", Assert.Single(changed.Files).Path);
        Assert.Throws<HexLinkException>(() => digests.Reconstruct(changed, new JsonArray(JsonValue.Create("work/unknown")), offer, 2));

        HexLinkManifest restored = await peer.WatchTree(basis.Folder, _ => { }, basis: basis);
        Assert.True(restored.Certified);
        Assert.Equal(2, restored.Files.Count);
        Assert.Equal(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("next")), restored.Files.Single(each => each.Path == "work/changed/two.txt").Hash);
    }

    private static string HashLarge(string path)
    {
        using FileStream file = HexLinkFileTransfer.Read(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file));
    }

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
