using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkWorkspaceRefreshTests
{
    [Fact]
    public async Task A_fresh_read_replaces_the_folders_branch_and_head_taken_at_hello()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        using var timeout = new CancellationTokenSource(WAIT_MS);
        Assert.Equal("work", Assert.Single(pair.PeerA.Workspace.Folders).Name);
        Assert.Empty(pair.PeerA.Workspace.Repositories);

        pair.A.Workspace = ChangedWorkspace(pair.A.Work);
        await pair.PeerA.RefreshWorkspace(timeout.Token);

        Assert.Equal("opened", Assert.Single(pair.PeerA.Workspace.Folders).Name);
        HexLinkRepository repository = Assert.Single(pair.PeerA.Workspace.Repositories);
        Assert.Equal(("topic", new string('a', 40)), (repository.Branch, repository.Head));
        Assert.Equal(string.Empty, repository.Path);
    }

    [Fact]
    public async Task A_change_hint_refreshes_viewers_and_a_revoked_viewer_cannot_refresh()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        using var timeout = new CancellationTokenSource(WAIT_MS);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.PeerA.Changed = () =>
        {
            if (pair.PeerA.Workspace.Name == "Changed")
                observed.TrySetResult();
        };

        pair.A.Workspace = ChangedWorkspace(pair.A.Work);
        pair.LinkA.WorkspaceMoved();
        await observed.Task.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS), timeout.Token);
        Assert.Equal("topic", Assert.Single(pair.PeerA.Workspace.Repositories).Branch);

        pair.BOnA = HexLinkRights.None;
        pair.LinkA.RightsMoved(pair.B.Device.Id);
        Assert.True(Eventually(() => pair.PeerA.Workspace.Folders.Count == 0));
        await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.RefreshWorkspace(timeout.Token));
        Assert.Empty(pair.PeerA.Workspace.Repositories);
    }

    [Fact]
    public async Task A_peer_that_ignores_the_new_hint_can_still_ask_with_the_original_hello()
    {
        using var timeout = new CancellationTokenSource(WAIT_MS);
        using var services = new LinkServices(DeviceA);
        using var oldServices = new LinkServices(DeviceB);
        using var link = new HexLink(services);
        var (send, receive) = Pair();
        using var legacy = new HexLinkSession(new HexLinkConnection(receive, DeviceA, "memory", false, () => HexLinkRights.View));
        using var legacyHost = new HexLinkHost(legacy, oldServices);
        var ignored = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        legacy.Requested = legacyHost.Handle;
        legacy.Told = (method, parameters) =>
        {
            if (method == "workspace/changed")
                ignored.TrySetResult(parameters);
        };
        legacy.Start();
        await link.Add(new HexLinkConnection(send, DeviceB, "memory", true, () => HexLinkRights.View), timeout.Token);

        services.Workspace = ChangedWorkspace(services.Work);
        link.WorkspaceMoved();
        Assert.Empty(await ignored.Task.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS), timeout.Token));

        var hello = Assert.IsType<JsonObject>(await legacy.Ask("link/hello", null, timeout.Token));
        Assert.Equal("Changed", hello["workspace"]?["name"]?.GetValue<string>());
    }

    private static HexLinkWorkspace ChangedWorkspace(string root) => new("Changed",
        [new HexLinkFolder("opened", root)], [new HexLinkRepository("opened", "topic", new string('a', 40), root)]);
}
