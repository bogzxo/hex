using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkPublicationTests
{
    [Fact]
    public async Task Authority_and_replica_exchange_one_checked_set_over_bulk_and_ack_once()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var a = new LinkServices(DeviceA);
        using var b = new LinkServices(DeviceB);
        var pin = new HexLinkAuthority(DeviceA.Id, 3);
        a.Workspace = a.Workspace with { Folders = [a.Workspace.Folders[0] with { Authority = pin }] };
        b.Workspace = b.Workspace with { Folders = [b.Workspace.Folders[0] with { Authority = pin }] };
        string storage = Path.Combine(Path.GetTempPath(), "hpub-" + Guid.NewGuid().ToString("N")[..12]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(a.Work, "file.txt"), "shared", stop.Token);
            await File.WriteAllTextAsync(Path.Combine(b.Work, "file.txt"), "shared", stop.Token);
            Guid id = Guid.NewGuid();
            int held = 0, installed = 0, acked = 0, notices = 0;
            var owner = new HexLinkWorktreePublication(a, Path.Combine(storage, "a"), _ => [DeviceB.Id],
                (_, set, peers, _) =>
                {
                    Assert.Equal(id, set.Id);
                    Assert.Equal(DeviceB.Id, Assert.Single(peers));
                    held++;
                    return Task.CompletedTask;
                },
                (_, set, device, _) =>
                {
                    Assert.Equal(id, set.Id);
                    Assert.Equal(DeviceB.Id, device);
                    acked++;
                    return Task.CompletedTask;
                },
                (_, _, _) => throw new InvalidOperationException(),
                draftPresence: (_, _) => Task.FromResult(new HexLinkDraftPresence(1, ["work/held.txt"])));
            var replica = new HexLinkWorktreePublication(b, Path.Combine(storage, "b"), _ => [],
                (_, _, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
                (_, set, _) =>
                {
                    Assert.Equal(id, set.Id);
                    installed++;
                    return Task.CompletedTask;
                }, received: set =>
                {
                    Assert.Equal(DeviceB.Name, set.Author);
                    notices++;
                });
            a.Publication = owner;
            b.Publication = replica;
            using var linksA = new HexLink(a);
            using var linksB = new HexLink(b);
            var (endA, endB) = Pair();
            const HexLinkRights edit = HexLinkRights.View | HexLinkRights.Edit;
            var connectingB = linksA.Add(new HexLinkConnection(endA, DeviceB, "memory", true, () => edit, bulk: HexLinkBulkSupport.Raw));
            var connectingA = linksB.Add(new HexLinkConnection(endB, DeviceA, "memory", false, () => edit, bulk: HexLinkBulkSupport.Raw));
            HexLinkPeer peerB = await connectingB.WaitAsync(stop.Token);
            HexLinkPeer peerA = await connectingA.WaitAsync(stop.Token);
            var visible = await peerA.DraftPresence("work", 3, stop.Token);
            Assert.Equal("work/held.txt", Assert.Single(visible.Paths));
            Assert.Equal(1, visible.Count);
            var route = replica.Route(b.Workspace.Folders[0], peerA);
            Assert.NotNull(route);

            byte[] first = "publish"u8.ToArray(), second = "new file"u8.ToArray();
            var edits = new HexLinkPublishEdit[]
            {
                new("work/file.txt", HexLinkFiles.Hash("shared"u8.ToArray()), first),
                new("work/new.txt", null, second)
            };
            Assert.False(File.Exists(Path.Combine(a.Work, "new.txt")));
            Assert.Null(await route.Status(id, stop.Token));
            var receipt = await route.Send(id, edits, true, stop.Token);
            Assert.Equal(2, receipt.Files);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(a.Work, "file.txt"), stop.Token));
            Assert.Equal("shared", await File.ReadAllTextAsync(Path.Combine(b.Work, "file.txt"), stop.Token));
            Assert.Contains(a.Audited, line => line.What == "publish-anyway");
            Assert.Equal(1, held);
            await owner.Deliver(peerB, stop.Token);
            await owner.Deliver(peerB, stop.Token);
            Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(b.Work, "file.txt"), stop.Token));
            Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(b.Work, "new.txt"), stop.Token));
            Assert.Equal(1, installed);
            Assert.Equal(1, acked);
            Assert.Equal(1, notices);
        }
        finally
        {
            if (Directory.Exists(storage))
            {
                foreach (string file in Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(storage, true);
            }
        }
    }

    [Fact]
    public async Task Acknowledgement_saves_the_pair_before_releasing_only_its_receipt()
    {
        const HexLinkRights edit = HexLinkRights.View | HexLinkRights.Edit;
        using var pair = await LinkedPair.Up(edit, edit);
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            Quiet = TimeSpan.FromHours(1), Rescan = TimeSpan.FromHours(1)
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sync.Start(cancel: stop.Token);
        Assert.True(sync.PublicationPairReady);
        string hash = HexLinkFiles.Hash("published"u8.ToArray());
        var first = new HexLinkCommittedSet(Guid.NewGuid(), "work", "", 1, HexLinkFenceKind.Publication,
            [new HexLinkCommittedFile("work/file", null, hash)]);
        var second = first with { Id = Guid.NewGuid() };
        sync.DeferPublication(first);
        sync.DeferPublication(second);
        Assert.True(sync.DefersPublication("work/file"));
        Assert.False(sync.DefersPublication("work/other"));
        sync.InstallPublicationBaseline(first);
        var state = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(sync.StateFile, stop.Token));
        Assert.Equal(hash, state!["bases"]!["work/file"]!["local"]!.GetValue<string>());
        Assert.Equal(hash, state["bases"]!["work/file"]!["remote"]!.GetValue<string>());
        sync.ReleasePublication(first);
        Assert.True(sync.DefersPublication("work/file"));
        sync.ReleasePublication(second);
        Assert.False(sync.DefersPublication("work/file"));
    }

    [Fact]
    public async Task Manifest_hash_rejects_a_different_blob_even_inside_an_intact_stream()
    {
        byte[] bytes = [1, 2, 3];
        var set = new HexLinkCommittedSet(Guid.NewGuid(), "work", "", 1, HexLinkFenceKind.Publication,
            [new HexLinkCommittedFile("work/file", null, HexLinkFiles.Hash(bytes))]);
        var packet = HexLinkPublicationPack.Encode(set, [new("work/file", null, bytes)]);
        using var stream = new MemoryStream(packet.Parts.SelectMany(part => part).ToArray());
        stream.Position = stream.Length - 1;
        stream.WriteByte(42);
        stream.Position = 0;
        await Assert.ThrowsAsync<HexLinkException>(() => HexLinkPublicationPack.Read(stream, packet.Length, default));
    }
}
