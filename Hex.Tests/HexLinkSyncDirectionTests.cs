using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Fact]
    public async Task The_bound_owner_sends_changes_but_never_applies_a_view_only_replicas_changes()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View, EDIT);
        Put(pair.A, "shared.txt", "base");
        Put(pair.B, "shared.txt", "base");
        using var engine = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            BoundAuthorityEngine = true
        };

        await engine.Start();
        await Settled(engine);
        Put(pair.A, "shared.txt", "edited by the owner");
        Put(pair.B, "replica.txt", "edited by the replica");
        await Settled(engine);

        Assert.Equal("edited by the owner", Get(pair.B, "shared.txt"));
        Assert.False(Has(pair.A, "replica.txt"));
        Assert.Equal("edited by the replica", Get(pair.B, "replica.txt"));
        Assert.Contains(engine.Skipped, row => row.Path == "work/replica.txt" && row.Why == "not sent: view only");

        pair.BOnA = EDIT;
        engine.RefreshReceiveRights();
        await Settled(engine);
        Assert.Equal("edited by the replica", Get(pair.A, "replica.txt"));
    }

    [Fact]
    public async Task A_bound_pull_mirror_does_not_send_edits_even_if_the_peer_grants_edit()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        Put(pair.A, "local.txt", "keep on this side");
        Put(pair.B, "authority.txt", "copy to replica");
        using var mirror = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "state"))
        {
            BoundPullMirror = true
        };
        await mirror.Start();
        await Settled(mirror);

        Assert.Equal(HexLinkSyncState.PullOnly, mirror.State);
        Assert.Equal("copy to replica", Get(pair.A, "authority.txt"));
        Assert.False(Has(pair.B, "local.txt"));
    }

    [Fact]
    public async Task Revoking_Edit_stops_that_devices_saves_landing_here_and_this_devices_still_go_there()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync onA = SyncOf(pair);
        using HexLinkSync onB = SyncOfB(pair);
        await Task.WhenAll(onA.Start(), onB.Start());
        Put(pair.A, "first.txt", "before");
        Assert.True(await Until(() => Get(pair.B, "first.txt") == "before"), onA.Summary + " / " + onB.Summary);

        // B takes Edit away from A: what A saves must stop landing on B, what B saves must still land on A
        pair.AOnB = HexLinkRights.View;
        pair.LinkB.RightsMoved(DeviceA.Id);
        onB.RefreshReceiveRights();
        Assert.True(await Until(() => onA.State == HexLinkSyncState.PullOnly), onA.Summary);
        Assert.Contains("Laptop", onA.Why);
        Assert.True(onB.ReceiveBlocked);
        Assert.Contains("Desk", onB.Summary);

        Put(pair.A, "from-a.txt", "A saved");
        Put(pair.A, "first.txt", "A edited");
        Put(pair.B, "from-b.txt", "B saved");
        Assert.True(await Until(() => Get(pair.A, "from-b.txt") == "B saved"), onA.Summary + " / " + onB.Summary);
        await Task.WhenAll(Settled(onA), Settled(onB));
        await Task.Delay(500);
        Assert.False(Has(pair.B, "from-a.txt"));
        Assert.Equal("before", Get(pair.B, "first.txt"));

        // Given back, what A saved meanwhile arrives
        pair.AOnB = EDIT;
        pair.LinkB.RightsMoved(DeviceA.Id);
        onB.RefreshReceiveRights();
        Assert.True(await Until(() => Get(pair.B, "from-a.txt") == "A saved" && Get(pair.B, "first.txt") == "A edited"),
            onA.Summary + " / " + onB.Summary);
    }

    [Fact]
    public async Task Revoking_Edit_the_other_way_stops_that_devices_saves_landing_there()
    {
        using var pair = await LinkedPair.Up(EDIT, EDIT);
        using HexLinkSync onA = SyncOf(pair);
        using HexLinkSync onB = SyncOfB(pair);
        await Task.WhenAll(onA.Start(), onB.Start());

        // A takes Edit away from B
        pair.BOnA = HexLinkRights.View;
        pair.LinkA.RightsMoved(DeviceB.Id);
        onA.RefreshReceiveRights();
        Assert.True(await Until(() => onB.State == HexLinkSyncState.PullOnly), onB.Summary);
        Assert.Contains("Desk", onB.Why);

        Put(pair.B, "from-b.txt", "B saved");
        Put(pair.A, "from-a.txt", "A saved");
        Assert.True(await Until(() => Get(pair.B, "from-a.txt") == "A saved"), onA.Summary + " / " + onB.Summary);
        await Task.WhenAll(Settled(onA), Settled(onB));
        await Task.Delay(500);
        Assert.False(Has(pair.A, "from-b.txt"));
    }

    [Fact]
    public void A_refusal_for_a_file_open_in_a_shared_document_carries_a_stable_marker_and_says_what_to_do()
    {
        string said = HexLinkSync.SharedOpenRefusal("ws/src/main.py");
        Assert.True(HexLinkSync.IsSharedOpenRefusal(said));
        Assert.Contains("Close its tab", said);
        Assert.Contains("ws/src/main.py", said);
        Assert.False(HexLinkSync.IsSharedOpenRefusal("ws/src/main.py has unsaved changes in the editor."));
        Assert.False(HexLinkSync.IsSharedOpenRefusal(null));
    }

    [Fact]
    public void A_refusal_caused_by_an_open_tab_carries_the_marker_and_one_for_anything_else_does_not()
    {
        var open = new HexLinkException(HexLinkErrors.CONFLICT, "ws/a.txt has unsaved changes in the editor.", new JsonObject { ["editor_dirty"] = true });
        Assert.True(HexLinkSync.IsSharedOpenRefusal(HexLinkSync.OpenTabMarked(open)));
        var shared = new HexLinkException(HexLinkErrors.CONFLICT, HexLinkSync.SharedOpenRefusal("ws/a.txt"), new JsonObject { ["editor_dirty"] = true });
        Assert.Equal(shared.Message, HexLinkSync.OpenTabMarked(shared));
        var other = new HexLinkException(HexLinkErrors.CONFLICT, "ws/a.txt changed since it was read.");
        Assert.False(HexLinkSync.IsSharedOpenRefusal(HexLinkSync.OpenTabMarked(other)));
    }

    [Fact]
    public async Task Activation_requires_view_and_a_direct_link()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.None, EDIT);
        var parameters = new JsonObject { ["folder"] = "work", ["authority"] = pair.A.Device.Id, ["epoch"] = 1, ["action"] = "ready" };
        var denied = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Session.Ask("sync/activate", (JsonObject)parameters.DeepClone()));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, denied.Code);
        pair.BOnA = HexLinkRights.View;
        pair.LinkA.RightsMoved(pair.B.Device.Id);
        Assert.True(await Until(() => pair.PeerA.Granted == HexLinkRights.View));

        pair.A.Workspace = pair.A.Workspace with
        {
            Folders = [pair.A.Workspace.Folders[0] with { Authority = new(pair.A.Device.Id, 2) }]
        };
        pair.A.SyncActivation = (caller, folder, action) =>
        {
            Assert.Same(pair.PeerB, caller);
            Assert.Equal("work", folder.Name);
            Assert.Equal("ready", action);
            return Task.FromResult<HexLinkSyncReport?>(null);
        };
        var epoch = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Session.Ask("sync/activate", (JsonObject)parameters.DeepClone()));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, epoch.Code);
        parameters["epoch"] = 2;

        parameters["relay"] = new JsonObject { ["hops"] = 1, ["origin"] = new string('1', 64) };
        var relay = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Session.Ask("sync/activate", (JsonObject)parameters.DeepClone()));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, relay.Code);
        Assert.Equal("Workspace sync activation requires a direct link.", relay.Message);
        Assert.Null(relay.Detail);
        parameters.Remove("relay");
        Assert.Null(await pair.PeerA.ActivateSync("work", new(pair.A.Device.Id, 2), "ready", CancellationToken.None));
    }
}

internal sealed partial class LinkServices : IHexLinkSyncActivation
{
    public Func<HexLinkPeer, HexLinkFolder, string, Task<HexLinkSyncReport?>>? SyncActivation { get; set; }

    public Task<HexLinkSyncReport?> ActivateSync(HexLinkPeer peer, HexLinkFolder folder, string action, bool resume, CancellationToken cancel) =>
        SyncActivation?.Invoke(peer, folder, action) ?? throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "No activation fixture.");
}
