using System.Text.Json.Nodes;
using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkClaimsTests
{
    private static readonly HexLinkClaimScope Scope = new("work", new string('D', 64));

    [Fact]
    public void Lease_sequences_use_receiver_time_and_an_expired_feed_requires_a_snapshot()
    {
        var table = new HexLinkClaims();
        var frame = Frame(DeviceB, "src/app.cs", 1, snapshot: true);
        Assert.Equal(HexLinkClaimReceipt.Changed, table.Receive(frame, DeviceB.Id, 1000));
        Assert.Single(table.At(Scope, "SRC/APP.cs", false, 1001));
        Assert.Equal(HexLinkClaimReceipt.Gap, table.Receive(frame with { Snapshot = false, Sequence = 3 }, DeviceB.Id, 1100));
        Assert.Equal(HexLinkClaimReceipt.Unchanged, table.Receive(frame with { Snapshot = false, Add = [], Heartbeat = true }, DeviceB.Id, 11_000));
        Assert.Equal(46_000, Assert.Single(table.At(Scope, "src/app.cs", false, 20_000)).ExpiresTick);
        table.Disconnect(DeviceB.Id, 22_000);
        Assert.Single(table.At(Scope, "src/app.cs", false, 36_999));
        Assert.Empty(table.At(Scope, "src/app.cs", false, 37_000));
        Assert.True(table.Expire(37_000));
        Assert.Equal(HexLinkClaimReceipt.Gap, table.Receive(frame with { Snapshot = false, Sequence = 3 }, DeviceB.Id, 38_000));
        Assert.Equal(HexLinkClaimReceipt.Changed, table.Receive(frame with { Sequence = 4 }, DeviceB.Id, 38_000));
    }

    [Fact]
    public void Prefixes_match_whole_segments_and_overlap_from_two_live_holders_is_preserved()
    {
        var table = new HexLinkClaims();
        var folder = Frame(DeviceA, "src", 1, snapshot: true, prefix: true);
        var file = Frame(DeviceB, "src/app.cs", 1, snapshot: true);
        table.Receive(folder, DeviceA.Id, 1000);
        table.Receive(file, DeviceB.Id, 1000);
        Assert.Equal(2, table.At(Scope, "src/app.cs", false, 1001).Count);
        Assert.Equal(2, table.At(Scope, "src", true, 1001).Count);
        Assert.Empty(table.At(Scope, "src-old/app.cs", false, 1001));
        Assert.Equal(HexLinkClaimReceipt.Changed, table.Receive(file with
        {
            Snapshot = false, Sequence = 2, Add = [], Drop = [file.Add[0].Draft.Key]
        }, DeviceB.Id, 1010));
        Assert.Single(table.At(Scope, "src/app.cs", false, 1011));
        Assert.False(HexLinkClaims.ValidPath("src/../secret", false));
        Assert.False(HexLinkClaims.ValidPath(".git/config", false));
    }

    [Fact]
    public void Wire_dates_are_durations_and_a_direct_frame_cannot_impersonate_a_device()
    {
        var frame = Frame(DeviceA, "src/app.cs", 1, snapshot: true);
        frame = frame with { Add = [frame.Add[0] with { SinceTick = 100, Draft = frame.Add[0].Draft with { EtaTick = 10_000 } }] };
        JsonObject wire = JsonNode.Parse(HexLinkClaims.Json(frame, 900, relay: false).ToJsonString())!.AsObject();
        Assert.Null(HexLinkClaims.Of(wire, DeviceB, 999_000));
        var read = Assert.IsType<HexLinkClaimFrame>(HexLinkClaims.Of(wire, DeviceA, 999_000));
        Assert.Equal(998_200, read.Add[0].SinceTick);
        Assert.Equal(1_008_100, read.Add[0].Draft.EtaTick);
        Assert.Equal(DeviceA.Id, read.Origin.Id);
    }

    [Fact]
    public async Task A_pinned_authority_relays_claims_to_another_spoke_and_relays_the_release()
    {
        using var hubServices = Services(DeviceA);
        using var firstServices = Services(DeviceB);
        using var secondServices = Services(DeviceC);
        using var hub = new HexLink(hubServices);
        using var first = new HexLink(firstServices);
        using var second = new HexLink(secondServices);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Connect(hub, DeviceA, first, DeviceB, deadline.Token);
        await Connect(hub, DeviceA, second, DeviceC, deadline.Token);

        foreach (HexLink link in new[] { hub, first, second })
            link.Claims.RefreshFolders();
        var scope = Assert.Single(first.Claims.Folders).Scope;
        var key = new HexLinkClaimKey("src/app.cs", false, 42, HexLinkClaimKind.Planned);
        var local = new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>
        {
            [scope] = new Dictionary<HexLinkClaimKey, HexLinkClaimDraft> { [key] = new(key, "Builder") }
        };
        await Until(() => second.Claims.Board.At(scope, key.Path, false, Environment.TickCount64)
            .Any(claim => claim.DeviceId == DeviceB.Id && claim.Via == DeviceA.Id), local);

        local.Clear();
        await Until(() => second.Claims.Board.At(scope, key.Path, false, Environment.TickCount64).Count == 0, local);

        async Task Until(Func<bool> done, Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>> drafts)
        {
            while (!done())
            {
                first.Claims.Update(drafts);
                hub.Claims.Update(new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>());
                second.Claims.Update(new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>());
                await Task.Delay(50, deadline.Token);
            }
        }
    }

    [Fact]
    public async Task A_folder_kept_in_sync_with_a_device_exchanges_claims_both_ways_without_an_authority()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View, HexLinkRights.View);
        pair.A.Chosen[DeviceB.Id] = ["work"];
        pair.B.Chosen[DeviceA.Id] = ["work"];
        var empty = new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>();
        pair.LinkA.Claims.RefreshFolders();
        pair.LinkB.Claims.RefreshFolders();
        var scope = Assert.Single(pair.LinkA.Claims.Folders).Scope;
        Assert.Equal(scope, Assert.Single(pair.LinkB.Claims.Folders).Scope);

        var mine = new HexLinkClaimKey("src/a.cs", false, 7, HexLinkClaimKind.Planned);
        var theirs = new HexLinkClaimKey("src/b.cs", false, 9, HexLinkClaimKind.Planned);
        var fromA = new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>> { [scope] = new() { [mine] = new(mine, "Builder") } };
        var fromB = new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>> { [scope] = new() { [theirs] = new(theirs, "Tester") } };
        Assert.True(Eventually(() =>
        {
            pair.LinkA.Claims.Update(fromA);
            pair.LinkB.Claims.Update(fromB);
            return pair.LinkB.Claims.Board.At(scope, mine.Path, false, Environment.TickCount64).Any(claim => claim.DeviceId == DeviceA.Id)
                && pair.LinkA.Claims.Board.At(scope, theirs.Path, false, Environment.TickCount64).Any(claim => claim.DeviceId == DeviceB.Id);
        }));

        // Not synced there, nothing is exchanged
        pair.B.Chosen.Clear();
        pair.A.Chosen.Clear();
        pair.LinkA.Claims.RefreshFolders();
        Assert.Empty(pair.LinkA.Claims.Folders);
    }

    [Fact]
    public void A_claim_keeps_the_time_it_was_first_made_across_changes_and_a_short_gap()
    {
        using var services = Services(DeviceA);
        using var link = new HexLink(services);
        link.Claims.RefreshFolders();
        var scope = Assert.Single(link.Claims.Folders).Scope;
        var key = new HexLinkClaimKey("src/a.cs", false, 7, HexLinkClaimKind.Planned);
        Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>> Drafts(string step) =>
            new() { [scope] = new() { [key] = new(key, "Builder", step) } };

        link.Claims.Update(Drafts("one"));
        long first = Assert.Single(link.Claims.Board.At(scope, key.Path, false, Environment.TickCount64)).SinceTick;
        Thread.Sleep(40);
        link.Claims.Update(Drafts("two"));
        Assert.Equal(first, Assert.Single(link.Claims.Board.At(scope, key.Path, false, Environment.TickCount64)).SinceTick);

        link.Claims.Update(new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>());
        Assert.Empty(link.Claims.Board.At(scope, key.Path, false, Environment.TickCount64));
        Thread.Sleep(40);
        link.Claims.Update(Drafts("two"));
        Assert.Equal(first, Assert.Single(link.Claims.Board.At(scope, key.Path, false, Environment.TickCount64)).SinceTick);
    }

    [Fact]
    public void Worktree_safety_claims_exclude_ignored_files_and_directory_prefixes()
    {
        using var services = Services(DeviceA);
        using var link = new HexLink(services);
        link.Claims.RefreshFolders();
        var scope = Assert.Single(link.Claims.Folders).Scope;
        var generated = new HexLinkClaimKey("generated/out.txt", false, 42, HexLinkClaimKind.Planned);
        var prefix = new HexLinkClaimKey("generated", true, 42, HexLinkClaimKind.Planned);
        var source = new HexLinkClaimKey("src/main.cs", false, 42, HexLinkClaimKind.Planned);
        var claims = new Dictionary<HexLinkClaimScope, Dictionary<HexLinkClaimKey, HexLinkClaimDraft>>
        {
            [scope] = new() { [generated] = new(generated, "Builder"), [prefix] = new(prefix, "Builder"),
                [source] = new(source, "Builder") }
        };

        link.Claims.Update(claims);
        Assert.NotEmpty(link.Claims.Board.At(scope, generated.Path, false, Environment.TickCount64));
        File.WriteAllText(Path.Combine(services.Work, HexLinkIgnore.FileName), "generated/\n");
        link.Claims.Update(claims);

        Assert.Empty(link.Claims.Board.At(scope, generated.Path, false, Environment.TickCount64));
        Assert.Empty(link.Claims.Board.At(scope, prefix.Path, true, Environment.TickCount64));
        Assert.Single(link.Claims.Board.At(scope, source.Path, false, Environment.TickCount64));
    }

    [Fact]
    public async Task Sixty_four_in_memory_links_hold_and_the_sixty_fifth_is_refused()
    {
        using var hostServices = new LinkServices(DeviceA);
        using var host = new HexLink(hostServices);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(24));
        var remotes = new List<(HexLink Link, LinkServices Services)>();
        try
        {
            for (int i = 1; i <= 64; i++)
            {
                var device = new HexLinkDevice(i.ToString("X64"), $"Peer {i}");
                var services = new LinkServices(device);
                var link = new HexLink(services);
                remotes.Add((link, services));
                await Connect(host, DeviceA, link, device, deadline.Token);
            }
            Assert.Equal(64, host.Peers.Count);
            var (a, b) = Pair();
            using var extra = b;
            var last = new HexLinkDevice(new string('F', 64), "Extra");
            var failure = await Assert.ThrowsAsync<HexLinkException>(() =>
                host.Add(new HexLinkConnection(a, last, "memory", false, () => HexLinkRights.View), deadline.Token));
            Assert.Equal(HexLinkErrors.BUSY, failure.Code);
            Assert.Equal(64, host.Peers.Count);
        }
        finally
        {
            foreach (var (link, services) in remotes)
            {
                link.Dispose();
                services.Dispose();
            }
        }
    }

    private static Task Connect(HexLink host, HexLinkDevice hostDevice, HexLink remote, HexLinkDevice remoteDevice, CancellationToken cancel)
    {
        var (a, b) = Pair();
        return Task.WhenAll(
            host.Add(new HexLinkConnection(a, remoteDevice, "memory", false, () => HexLinkRights.View), cancel),
            remote.Add(new HexLinkConnection(b, hostDevice, "memory", true, () => HexLinkRights.View), cancel));
    }

    private static LinkServices Services(HexLinkDevice device)
    {
        var services = new LinkServices(device);
        services.Workspace = services.Workspace with { Folders = [services.Workspace.Folders[0] with { Authority = new HexLinkAuthority(DeviceA.Id, 1) }] };
        return services;
    }

    private static HexLinkClaimFrame Frame(HexLinkDevice device, string path, long sequence, bool snapshot, bool prefix = false) =>
        new(Scope, device, "00000000000000000000000000000002", sequence, snapshot, HexLinkClaims.LEASE_MS,
            [new(new HexLinkClaimDraft(new HexLinkClaimKey(path, prefix, 12, HexLinkClaimKind.Planned), "Editor"), 0)], []);
}
