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
        parameters.Remove("relay");
        Assert.Null(await pair.PeerA.ActivateSync("work", new(pair.A.Device.Id, 2), "ready", CancellationToken.None));
    }
}

internal sealed partial class LinkServices : IHexLinkSyncActivation
{
    public Func<HexLinkPeer, HexLinkFolder, string, Task<HexLinkSyncReport?>>? SyncActivation { get; set; }

    public Task<HexLinkSyncReport?> ActivateSync(HexLinkPeer peer, HexLinkFolder folder, string action, bool resume, CancellationToken cancel) =>
        SyncActivation?.Invoke(peer, folder, action) ?? throw HexLinkException.NotAllowed("No activation fixture.");
}
