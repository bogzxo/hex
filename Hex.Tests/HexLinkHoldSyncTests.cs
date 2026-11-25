using System.Text;
using System.Text.Json.Nodes;
using Hex.Core;
using Hex.Core.Git;
using Horizon.Core.Platform;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

internal sealed partial class LinkServices : IHexLinkHeldSubmission
{
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> SubmittedPaths = new();

    public async Task<string?> ApplyHeldSubmission(string full, string wire, string? basis, byte[]? bytes, CancellationToken cancel,
        bool? executable, HexLinkFiles.PackDirectories? directories)
    {
        string? hash = await HexLinkFiles.CompareAndReplace(full, wire, basis, bytes, cancel, executable);
        SubmittedPaths.Enqueue(wire);
        return hash;
    }

    internal HexLinkHoldStore? HoldStore { get; set; }

    public HexLinkHoldStore? Holds(HexLinkFolder folder) =>
        HoldStore is not null && PlatformPaths.Same(folder.Path, Work) ? HoldStore : null;
}

public sealed class HexLinkHoldSyncTests
{
    private const HexLinkRights Edit = HexLinkRights.View | HexLinkRights.Edit;
    private const string Wire = "work/a.txt";
    private const string Baseline = "the baseline\n";
    private const string Private = "local work in progress\n";
    private const string Shared = "incoming from the other device\n";

    [Fact]
    public void Shared_folders_are_the_synced_or_holding_ones_never_just_the_first()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hex-shared-{Guid.NewGuid():N}");
        try
        {
            var first = new HexLinkFolder("FreshImageDemo", Directory.CreateDirectory(Path.Combine(root, "first")).FullName);
            var second = new HexLinkFolder("Shared", Directory.CreateDirectory(Path.Combine(root, "second")).FullName);
            var workspace = new HexLinkWorkspace("Test", [first, second], []);
            var store = new HexLinkHoldStore(second, Path.Combine(root, "sync"));

            // Nothing synced or held: nothing is shared, the callers fall back to the first folder
            Assert.Empty(Hex.Dawn.DawnScene.LinkSharedFolders(workspace, _ => null, []));
            Assert.Equal("FreshImageDemo", Hex.Dawn.DawnScene.LinkDefaultSyncFolder(workspace, [], []));

            // The second folder is synced: it is the shared one, and the sync's default
            var synced = Hex.Dawn.DawnScene.LinkSharedFolders(workspace, _ => null, ["shared"]);
            Assert.Equal("Shared", Assert.Single(synced).Name);
            Assert.Equal("Shared", Hex.Dawn.DawnScene.LinkDefaultSyncFolder(workspace, ["Shared"], synced));
            Assert.Equal("Shared", Hex.Dawn.DawnScene.LinkDefaultSyncFolder(workspace, [], synced));

            // A folder with a held file is shared too, so hold list covers it
            store.Hold("Shared/a.txt", Bytes("x"), HexLinkHoldBaseSource.Agreed);
            var held = Hex.Dawn.DawnScene.LinkSharedFolders(workspace, folder => folder.Name == "Shared" ? store : null, []);
            Assert.Equal("Shared", Assert.Single(held).Name);

            // Both: both are listed
            Assert.Equal(2, Hex.Dawn.DawnScene.LinkSharedFolders(workspace, folder => folder.Name == "Shared" ? store : null, ["FreshImageDemo"]).Count);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Remote_hex_read_uses_shared_text_and_symbols_for_a_held_file()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        const string wire = "work/Example.cs";
        string file = Path.Combine(pair.A.Work, "Example.cs");
        const string baseline = "class SharedBaseline { }\n";
        const string working = "class PrivateDraft { }\n";
        var index = new HexCodeIndex(() => [pair.A.Work]);
        pair.A.LinkTools.ReadIndex = index;
        EnableHolds(pair.A).Hold(wire, Bytes(baseline), HexLinkHoldBaseSource.Agreed);
        File.WriteAllText(file, working);
        index.Look(force: true);
        Assert.Contains("PrivateDraft", index.Read(file, null, null, null, false, 2000).Text);

        var ranged = await pair.PeerA.Call("hex_read", new() { ["path"] = wire });
        Assert.False(ranged.IsError);
        Assert.Contains("held on Desk: showing the last shared version", ranged.AllText());
        Assert.Contains(baseline.Trim(), ranged.AllText());
        Assert.DoesNotContain("PrivateDraft", ranged.AllText());

        var symbol = await pair.PeerA.Call("hex_read", new() { ["path"] = file, ["symbol"] = "SharedBaseline" });
        Assert.False(symbol.IsError);
        Assert.Contains("class SharedBaseline { }", symbol.AllText());
        Assert.DoesNotContain("PrivateDraft", symbol.AllText());

        var privateSymbol = await pair.PeerA.Call("hex_read", new() { ["path"] = file, ["symbol"] = "PrivateDraft" });
        Assert.True(privateSymbol.IsError);
        Assert.DoesNotContain(working.Trim(), privateSymbol.AllText());
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Call("hex_link_hold", new() { ["action"] = "list" })));
    }

    [Fact]
    public async Task A_file_held_again_straight_after_a_submit_starts_from_what_was_submitted()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        HexLinkHoldStore holds = EnableHolds(pair.A);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);
        var service = new HexLinkHoldService(pair.A.Workspace.Folders[0], holds) { AgreedBasis = sync.HoldBasis };
        string before = (await service.Hold(Wire)).BaseHash!;
        File.WriteAllText(Local(pair.A), Private);
        Assert.Equal(Wire, Assert.Single((await service.Submit(check: false)).Paths));

        var again = await service.Hold(Wire);
        Assert.NotEqual(before, again.BaseHash);
        Assert.Equal(HexLinkFiles.Hash(Bytes(Private)), again.BaseHash);
    }

    [Fact]
    public async Task A_two_way_sync_stays_two_way_when_both_folders_are_pinned_to_one_authority()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        var pin = new HexLinkAuthority(DeviceA.Id, 1);
        foreach (LinkServices side in new[] { pair.A, pair.B })
            side.Workspace = new HexLinkWorkspace("Test", [side.Workspace.Folders[0] with { Authority = pin }], []);
        Both(pair, Baseline);

        // The engine a legacy pair runs, which the pin leaves as it is: no bound flags, nothing pull only
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);
        Assert.NotEqual(HexLinkSyncState.PullOnly, sync.State);

        File.WriteAllText(Local(pair.A), "from the authority\n");
        await Until(() => File.ReadAllText(Local(pair.B)) == "from the authority\n", sync);
        File.WriteAllText(Local(pair.B), "from the other device\n");
        await Until(() => File.ReadAllText(Local(pair.A)) == "from the other device\n", sync);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_checked_hold_resolution_is_marked_for_the_open_peer_editor(bool packed)
    {
        using var pair = await LinkedPair.Up(Edit, Edit, bulk: packed ? HexLinkBulkSupport.Raw : HexLinkBulkSupport.None);
        Both(pair, Baseline);
        HexLinkHoldStore holds = EnableHolds(pair.A);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);
        var service = new HexLinkHoldService(pair.A.Workspace.Folders[0], holds) { AgreedBasis = sync.HoldBasis };
        await service.Hold(Wire);
        File.WriteAllText(Local(pair.A), Private);
        File.WriteAllText(Local(pair.B), Shared);
        await Until(() => holds.Get(Wire)?.HasIncoming == true, sync);

        var conflict = Assert.Single((await service.Submit()).NeedsMerge);
        const string resolved = "hand resolved on the holder\n";
        await conflict.Session!.Complete(Bytes(resolved));
        await Flush(sync);

        Assert.Empty(sync.Conflicts);
        Assert.Equal(resolved, File.ReadAllText(Local(pair.B)));
        Assert.Equal(Wire, Assert.Single(pair.B.SubmittedPaths));
        File.WriteAllText(Local(pair.A), "ordinary change after submission\n");
        await Flush(sync);
        Assert.Equal("ordinary change after submission\n", File.ReadAllText(Local(pair.B)));
        Assert.Equal(Wire, Assert.Single(pair.B.SubmittedPaths));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forwarded_submission_survives_a_sync_pass_on_the_previous_contents(bool packed)
    {
        using var pair = await LinkedPair.Up(Edit, Edit, bulk: packed ? HexLinkBulkSupport.Raw : HexLinkBulkSupport.None);
        Both(pair, Baseline);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);

        const string before = "the checked write will replace this version\n";
        const string submitted = "exact checked result from the first peer\n";
        sync.RememberHeldSubmission(Wire, new(HexLinkFiles.Hash(Bytes(submitted)), HexLinkFiles.Hash(Bytes(before)), Forwarded: true));

        // Give the worker a complete pass on the predecessor before the checked write lands.
        File.WriteAllText(Local(pair.A), before);
        await Flush(sync);
        Assert.Equal(before, File.ReadAllText(Local(pair.B)));
        Assert.Empty(pair.B.SubmittedPaths);

        File.WriteAllText(Local(pair.A), submitted);
        await Flush(sync);
        Assert.Equal(submitted, File.ReadAllText(Local(pair.B)));
        Assert.Empty(sync.Conflicts);
        Assert.Equal(Wire, Assert.Single(pair.B.SubmittedPaths));

        // A later ordinary save of identical bytes must not inherit the consumed receipt.
        File.WriteAllText(Local(pair.A), before);
        await Flush(sync);
        File.WriteAllText(Local(pair.A), submitted);
        await Flush(sync);
        Assert.Equal(submitted, File.ReadAllText(Local(pair.B)));
        Assert.Equal(Wire, Assert.Single(pair.B.SubmittedPaths));
    }

    [Fact]
    public async Task An_unrelated_change_invalidates_a_forwarded_receipt()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);

        const string submitted = "a checked result that has not reached disk\n";
        const string unrelated = "a newer independent edit\n";
        sync.RememberHeldSubmission(Wire, new(HexLinkFiles.Hash(Bytes(submitted)), HexLinkFiles.Hash(Bytes(Baseline)), Forwarded: true));
        File.WriteAllText(Local(pair.A), unrelated);
        await Flush(sync);
        Assert.Equal(unrelated, File.ReadAllText(Local(pair.B)));

        File.WriteAllText(Local(pair.A), submitted);
        await Flush(sync);
        Assert.Equal(submitted, File.ReadAllText(Local(pair.B)));
        Assert.Empty(pair.B.SubmittedPaths);
    }

    [Fact]
    public async Task Clearing_an_older_receipt_preserves_a_new_receipt_for_identical_bytes()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);

        const string submitted = "the same checked result arrived twice\n";
        var older = new HexLinkSync.HeldSubmission(HexLinkFiles.Hash(Bytes(submitted)), HexLinkFiles.Hash(Bytes(Baseline)), Forwarded: true);
        sync.RememberHeldSubmission(Wire, older);
        sync.RememberHeldSubmission(Wire, older with { });
        sync.ForgetHeldSubmission(Wire, older);

        File.WriteAllText(Local(pair.A), submitted);
        await Flush(sync);
        Assert.Equal(submitted, File.ReadAllText(Local(pair.B)));
        Assert.Equal(Wire, Assert.Single(pair.B.SubmittedPaths));
    }

    [Fact]
    public async Task Holder_keeps_its_edits_records_peer_changes_and_leaves_other_files_live()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        HexLinkHoldStore holds = EnableHolds(pair.A);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);
        holds.Hold(Wire, Bytes(Baseline), HexLinkHoldBaseSource.Agreed);
        File.WriteAllText(Local(pair.A), Private);
        await Flush(sync);
        Assert.Equal(Baseline, File.ReadAllText(Local(pair.B)));
        Assert.Equal("Held on this device", Assert.Single(sync.Skipped).Why);
        Assert.Equal(1, sync.Held);
        Assert.Contains("1 held", sync.Summary);

        File.WriteAllText(Local(pair.B), Shared);
        File.WriteAllText(Path.Combine(pair.B.Work, "from-b.txt"), "B to A");
        File.WriteAllText(Path.Combine(pair.A.Work, "from-a.txt"), "A to B");
        await Flush(sync);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
        Assert.Equal(Shared, File.ReadAllText(Local(pair.B)));
        Assert.Equal(Bytes(Shared), holds.Read(Wire)!.IncomingBytes);
        Assert.Contains("others changed it", Assert.Single(sync.Skipped).Why);
        Assert.Empty(sync.Conflicts);
        Assert.Equal("B to A", File.ReadAllText(Path.Combine(pair.A.Work, "from-b.txt")));
        Assert.Equal("A to B", File.ReadAllText(Path.Combine(pair.B.Work, "from-a.txt")));

        File.Delete(Local(pair.B));
        await Flush(sync);
        Assert.True(holds.Get(Wire)!.IncomingDeleted);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task The_peer_of_a_holder_knows_the_file_is_held_until_it_is_released()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        HexLinkHoldStore holds = EnableHolds(pair.A);
        using var sync = Sync(pair.B, pair.PeerA);
        await Ready(sync);
        Assert.Empty(sync.RemoteHeldPaths);
        Assert.False(sync.IsRemoteHeld(Wire));
        int version = sync.RemoteHoldsVersion;

        holds.Hold(Wire, Bytes(Baseline), HexLinkHoldBaseSource.Agreed);
        await Flush(sync);
        await Until(() => sync.IsRemoteHeld(Wire), sync);
        Assert.Equal(Wire, Assert.Single(sync.RemoteHeldPaths));
        Assert.True(sync.RemoteHoldsVersion > version);
        version = sync.RemoteHoldsVersion;

        holds.Release(Wire);
        await Flush(sync);
        await Until(() => !sync.IsRemoteHeld(Wire), sync);
        Assert.Empty(sync.RemoteHeldPaths);
        Assert.True(sync.RemoteHoldsVersion > version);
    }

    [Fact]
    public async Task Restart_with_a_persisted_hold_keeps_the_private_copy_and_incoming()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        var holds = EnableHolds(pair.A);
        using (var first = Sync(pair.A, pair.PeerB))
        {
            await Ready(first);
            holds.Hold(Wire, Bytes(Baseline), HexLinkHoldBaseSource.Agreed);
            File.WriteAllText(Local(pair.A), Private);
            File.WriteAllText(Local(pair.B), Shared);
            await Flush(first);
        }

        var restartedStore = EnableHolds(pair.A);
        using var sync = Sync(pair.A, pair.PeerB);
        await Ready(sync);
        Assert.Equal(1, sync.Held);
        Assert.Equal(Bytes(Shared), restartedStore.Read(Wire)!.IncomingBytes);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
        Assert.Equal(Shared, File.ReadAllText(Local(pair.B)));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task Peer_only_sync_sees_the_base_via_manifest_fetch_and_direct_read()
    {
        using var pair = await LinkedPair.Up(Edit, Edit, HexLinkBulkSupport.Raw);
        Both(pair, Baseline);
        HoldPrivate(pair.A);
        using var peerOnly = Sync(pair.B, pair.PeerA);
        await Ready(peerOnly);

        var manifest = await pair.PeerA.Manifest("work");
        var entry = Assert.Single(manifest.Files);
        Assert.Equal(HexLinkFiles.Hash(Bytes(Baseline)), entry.Hash);
        Assert.Equal(Bytes(Baseline).LongLength, entry.Size);
        Assert.Contains(Wire, manifest.HeldPaths!);
        Assert.Equal(Bytes(Baseline), (await pair.PeerA.Read(Wire)).Bytes);
        var listing = Assert.IsType<JsonObject>(await pair.PeerA.Session.Ask("fs/list", new JsonObject { ["path"] = "work" }));
        Assert.Equal(entry.Size, Assert.Single(Assert.IsType<JsonArray>(listing["entries"]))!["size"]!.GetValue<long>());
        Assert.Equal(Baseline, File.ReadAllText(Local(pair.B)));
        Assert.Contains(peerOnly.Skipped, row => row.Path == Wire && row.Why == $"Held on {DeviceA.Name}");
        Assert.Empty(peerOnly.Conflicts);

        File.WriteAllText(Local(pair.B), Shared);
        await Flush(peerOnly);
        Assert.Equal(Bytes(Shared), pair.A.HoldStore!.Read(Wire)!.IncomingBytes);
        Assert.Equal(Shared, File.ReadAllText(Local(pair.B)));
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
        Assert.Empty(peerOnly.Conflicts);
    }

    [Fact]
    public async Task Saving_a_held_file_does_not_emit_a_peer_tree_change()
    {
        using var pair = await LinkedPair.Up(Edit, Edit, HexLinkBulkSupport.Raw);
        HoldPrivate(pair.A);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<HexLinkTreeChange>();
        await pair.PeerA.WatchTree("work", batch =>
        {
            foreach (var change in batch)
                changes.Enqueue(change);
        });
        File.WriteAllText(Local(pair.A), Private + " another save\n");
        await pair.PeerA.Flush("work", CancellationToken.None);
        Assert.Empty(changes);
        Assert.Equal(Bytes(Baseline), (await pair.PeerA.Read(Wire)).Bytes);
    }

    [Fact]
    public async Task Direct_write_and_delete_acknowledge_held_versions_and_leave_the_file_in_place()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        HoldPrivate(pair.A);
        var write = new JsonObject
        {
            ["path"] = Wire, ["base"] = HexLinkFiles.Hash(Bytes(Baseline)),
            ["base64"] = Convert.ToBase64String(Bytes(Shared)), ["accept_held"] = true,
        };
        var reply = Assert.IsType<JsonObject>(await pair.PeerA.Session.Ask("fs/write", write));
        Assert.True(HexLinkData.Bool(reply, "held"));
        Assert.Equal(Bytes(Shared), pair.A.HoldStore!.Read(Wire)!.IncomingBytes);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));

        var delete = new JsonObject { ["path"] = Wire, ["base"] = HexLinkFiles.Hash(Bytes(Baseline)), ["accept_held"] = true };
        reply = Assert.IsType<JsonObject>(await pair.PeerA.Session.Ask("fs/delete", delete));
        Assert.True(HexLinkData.Bool(reply, "held"));
        Assert.True(pair.A.HoldStore.Get(Wire)!.IncomingDeleted);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));

        write = (JsonObject)write.DeepClone();
        write.Remove("accept_held");
        await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Session.Ask("fs/write", write));
        Assert.Equal(Bytes(Shared), pair.A.HoldStore.Read(Wire)!.IncomingBytes);
    }

    [Fact]
    public async Task File_packs_publish_base_bytes_and_capture_an_incoming_write()
    {
        using var pair = await LinkedPair.Up(Edit, Edit, HexLinkBulkSupport.Raw);
        HoldPrivate(pair.A);
        var published = await pair.PeerA.ReadFilePack([Wire], CancellationToken.None);
        Assert.Empty(published.Errors);
        Assert.Equal(Bytes(Baseline), published.Files[Wire].Bytes);

        var request = new HexLinkFilePack.Entry(Wire, Bytes(Shared), HexLinkFiles.Hash(Bytes(Baseline)));
        var response = await pair.PeerA.WriteFilePack("work", [request], CancellationToken.None);
        Assert.True(response[Wire].Held);
        Assert.Null(response[Wire].Error);
        Assert.Equal(Bytes(Shared), pair.A.HoldStore!.Read(Wire)!.IncomingBytes);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
    }

    [Fact]
    public async Task Streamed_reads_publish_base_bytes_and_capture_incoming_streams()
    {
        using var pair = await LinkedPair.Up(Edit, Edit, HexLinkBulkSupport.Raw);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        const string wire = "work/large.bin";
        byte[] before = new byte[HexLinkFiles.MOST_READ + 1];
        byte[] mine = (byte[])before.Clone();
        mine[^1] = 1;
        var holds = EnableHolds(pair.A);
        holds.Hold(wire, before, HexLinkHoldBaseSource.Agreed);
        File.WriteAllBytes(Path.Combine(pair.A.Work, "large.bin"), mine);
        var transfer = new HexLinkFileTransfer(wire, before.LongLength, HexLinkFiles.Hash(before));
        string downloaded = Path.Combine(pair.B.Root, "read.hexpart");
        await pair.PeerA.ReadStreamedFile(transfer, downloaded, stop.Token);
        Assert.Equal(transfer.Hash, HexLinkFiles.Hash(File.ReadAllBytes(downloaded)));

        byte[] incoming = (byte[])before.Clone();
        incoming[^1] = 2;
        string source = Path.Combine(pair.B.Work, "large.bin");
        File.WriteAllBytes(source, incoming);
        string hash = HexLinkFiles.Hash(incoming);
        var sent = await Assert.ThrowsAsync<HexLinkException>(() =>
            pair.PeerA.WriteStreamedFile(transfer with { Hash = hash, Base = transfer.Hash }, source, stop.Token));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, sent.Code);
        Assert.Equal(hash, holds.Get(wire)!.IncomingHash);
        Assert.Equal(1, File.ReadAllBytes(Path.Combine(pair.A.Work, "large.bin"))[^1]);
    }

    [Fact]
    public async Task A_held_new_file_is_absent_in_the_peer_tree_and_reads()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        var holds = EnableHolds(pair.A);
        holds.Hold(Wire, [], HexLinkHoldBaseSource.Agreed, baseDeleted: true);
        File.WriteAllText(Local(pair.A), Private);
        Assert.Empty((await pair.PeerA.Manifest("work")).Files);
        var listing = Assert.IsType<JsonObject>(await pair.PeerA.Session.Ask("fs/list", new JsonObject { ["path"] = "work" }));
        Assert.Empty(Assert.IsType<JsonArray>(listing["entries"]));
        Assert.True(await pair.PeerA.ConfirmAbsent("work", Wire, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Read(Wire));
        Assert.Equal(HexLinkErrors.NOT_FOUND, missing.Code);
    }

    [Fact]
    public async Task A_hold_does_not_interrupt_sync_between_two_other_devices()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        using var third = new LinkServices(DeviceC);
        using var thirdLink = new HexLink(third);
        Both(pair, Baseline);
        File.WriteAllText(Local(third), Baseline);
        HoldPrivate(pair.A);
        var (a, b) = Pair();
        Task<HexLinkPeer> peerC = pair.LinkB.Add(new HexLinkConnection(a, third.Device, "memory", true, () => Edit));
        Task<HexLinkPeer> peerB = thirdLink.Add(new HexLinkConnection(b, pair.B.Device, "memory", false, () => Edit));
        await Task.WhenAll(peerC, peerB).WaitAsync(TimeSpan.FromSeconds(20));

        using var holder = Sync(pair.A, pair.PeerB);
        using var others = Sync(pair.B, await peerC);
        await Ready(holder);
        await Ready(others);
        File.WriteAllText(Local(pair.B), Shared);
        await Flush(others);
        await Flush(holder);
        Assert.Equal(Shared, File.ReadAllText(Local(third)));
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
        Assert.Equal(Bytes(Shared), pair.A.HoldStore!.Read(Wire)!.IncomingBytes);
    }

    [Fact]
    public async Task A_commit_on_the_holder_waits_for_release()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        string baseline = Repository(pair);
        var holds = EnableHolds(pair.A);
        using var sync = Sync(pair.A, pair.PeerB, follow: true);
        await Ready(sync);
        holds.Hold(Wire, Bytes(Baseline), HexLinkHoldBaseSource.Agreed);
        File.WriteAllText(Local(pair.A), Private);
        Git(pair.A.Work, "commit", "-qam", "held edit");
        string next = Git(pair.A.Work, "rev-parse", "HEAD");

        await Until(() => sync.GitFollowIssue?.Contains("commit not sent: 1 file held", StringComparison.Ordinal) == true, sync);
        Assert.Equal(baseline, Git(pair.B.Work, "rev-parse", "HEAD"));
        Assert.Equal(Baseline, File.ReadAllText(Local(pair.B)));
        Assert.True(holds.Release(Wire));
        await Until(() => Git(pair.B.Work, "rev-parse", "HEAD") == next, sync);
        await Flush(sync);
        Assert.Equal(Private, File.ReadAllText(Local(pair.B)));
    }

    [Fact]
    public async Task Peer_commit_follows_and_its_held_file_becomes_incoming()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Repository(pair);
        var holds = EnableHolds(pair.A);
        using var sync = Sync(pair.A, pair.PeerB, follow: true);
        await Ready(sync);
        holds.Hold(Wire, Bytes(Baseline), HexLinkHoldBaseSource.Agreed);
        File.WriteAllText(Local(pair.A), Private);
        File.WriteAllText(Local(pair.B), Shared);
        Git(pair.B.Work, "commit", "-qam", "peer edit");
        string next = Git(pair.B.Work, "rev-parse", "HEAD");

        await Until(() => Git(pair.A.Work, "rev-parse", "HEAD") == next && holds.Get(Wire)!.HasIncoming, sync);
        Assert.Equal(Private, File.ReadAllText(Local(pair.A)));
        Assert.Equal(Bytes(Shared), holds.Read(Wire)!.IncomingBytes);
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task The_holder_submits_a_clean_merge_against_the_peer_version_it_received()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        const string basis = "a\nb\nc\nd\ne\n";
        Both(pair, basis);
        HexLinkHoldStore holds = EnableHolds(pair.A);
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "sync"))
        {
            Holds = holds, Quiet = TimeSpan.FromSeconds(3),
        };
        await Ready(sync);
        var service = new HexLinkHoldService(pair.A.Workspace.Folders[0], holds) { AgreedBasis = sync.HoldBasis };
        File.WriteAllText(Local(pair.A), "A\nb\nc\nd\ne\n");
        var entry = await service.Hold(Wire);
        Assert.Equal(HexLinkHoldBaseSource.Agreed, entry.BaseSource);
        Assert.Equal(Bytes(basis), holds.Read(Wire)!.BaseBytes);

        File.WriteAllText(Local(pair.B), "a\nb\nc\nd\nE\n");
        await Flush(sync);
        Assert.Equal(Wire, Assert.Single((await service.Submit()).Paths));
        await Flush(sync);
        Assert.Equal("A\nb\nc\nd\nE\n", File.ReadAllText(Local(pair.A)));
        Assert.Equal(File.ReadAllText(Local(pair.A)), File.ReadAllText(Local(pair.B)));
        Assert.Empty(sync.Conflicts);
    }

    [Fact]
    public async Task Hold_reuses_the_exact_head_blob_when_an_agreed_binary_base_is_not_cached()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        byte[] basis = [0, 1, 2, 255];
        Repository(pair, basis);
        var holds = EnableHolds(pair.A);
        var folder = pair.A.Workspace.Folders[0];
        using var sync = new HexLinkSync(pair.PeerB, folder, Path.Combine(pair.A.Root, "sync"))
        {
            Holds = holds, Quiet = TimeSpan.FromSeconds(3),
        };
        await Ready(sync);
        File.WriteAllBytes(Local(pair.A), [0, 3, 4, 255]);
        var service = new HexLinkHoldService(folder, holds) { AgreedBasis = sync.HoldBasis };
        Assert.Equal(HexLinkHoldBaseSource.Git, (await service.Hold(Wire)).BaseSource);
        Assert.Equal(basis, holds.Read(Wire)!.BaseBytes);
    }

    [Fact]
    public async Task A_peer_only_sync_accepts_the_completed_resolution_for_the_version_it_offered()
    {
        using var pair = await LinkedPair.Up(Edit, Edit);
        Both(pair, Baseline);
        HoldPrivate(pair.A);
        using var sync = Sync(pair.B, pair.PeerA);
        await Ready(sync);
        File.WriteAllText(Local(pair.B), Shared);
        await Flush(sync);
        var service = new HexLinkHoldService(pair.A.Workspace.Folders[0], pair.A.HoldStore!);
        var conflict = Assert.Single((await service.Submit()).NeedsMerge);
        const string resolved = "the chosen resolution\n";
        await conflict.Session!.Complete(Bytes(resolved));
        await Flush(sync);
        Assert.Equal(resolved, File.ReadAllText(Local(pair.A)));
        Assert.Equal(resolved, File.ReadAllText(Local(pair.B)));
        Assert.Empty(sync.Conflicts);
    }

    private static HexLinkHoldStore EnableHolds(LinkServices services) =>
        services.HoldStore = new(services.Workspace.Folders[0], Path.Combine(services.Root, "sync"));

    private static void HoldPrivate(LinkServices services)
    {
        EnableHolds(services).Hold(Wire, Bytes(Baseline), HexLinkHoldBaseSource.Agreed);
        File.WriteAllText(Local(services), Private);
    }

    private static HexLinkSync Sync(LinkServices local, HexLinkPeer peer, bool follow = false) =>
        new(peer, local.Workspace.Folders[0], Path.Combine(local.Root, "sync"))
        {
            Holds = local.HoldStore, Quiet = TimeSpan.FromMilliseconds(70), Rescan = TimeSpan.FromSeconds(1),
            GitFollowEnabled = follow ? static () => true : null, GitFollowData = local.GitFollowData,
        };

    private static async Task Ready(HexLinkSync sync)
    {
        await sync.Start().WaitAsync(TimeSpan.FromSeconds(40));
        await Flush(sync);
        Assert.Equal(HexLinkSyncState.Idle, sync.State);
    }

    private static Task Flush(HexLinkSync sync) => sync.Flush().WaitAsync(TimeSpan.FromSeconds(25));

    private static Task Until(Func<bool> condition, HexLinkSync sync)
    {
        Assert.True(Eventually(condition, 35_000), sync.Summary);
        return Task.CompletedTask;
    }

    private static void Both(LinkedPair pair, string text)
    {
        File.WriteAllText(Local(pair.A), text);
        File.WriteAllText(Local(pair.B), text);
    }

    private static string Repository(LinkedPair pair, byte[]? initialBytes = null)
    {
        Git(pair.A.Work, "init", "-q", "-b", "development");
        Configure(pair.A.Work);
        File.WriteAllBytes(Local(pair.A), initialBytes ?? Bytes(Baseline));
        Git(pair.A.Work, "add", ".");
        Git(pair.A.Work, "commit", "-qm", "baseline");
        Git(pair.B.Root, "-c", "core.autocrlf=false", "clone", "--quiet", "--no-local", pair.A.Work, pair.B.Work);
        Configure(pair.B.Work);
        pair.A.FollowGit = pair.B.FollowGit = true;
        return Git(pair.A.Work, "rev-parse", "HEAD");
    }

    private static void Configure(string path)
    {
        Git(path, "config", "core.autocrlf", "false");
        Git(path, "config", "user.name", "Link Test");
        Git(path, "config", "user.email", "link-test@example.test");
    }

    private static string Git(string root, params string[] args)
    {
        GitResult result = GitProcess.Run(root, args, patience: TimeSpan.FromSeconds(15));
        Assert.True(result.Ok, result.Why);
        return result.Output.Trim();
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Local(LinkServices services) => Path.Combine(services.Work, "a.txt");
}
