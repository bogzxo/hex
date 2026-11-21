using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkAuthorityTests
{
    [Fact]
    public void Bindings_survive_disconnect_and_restart_and_transfer_refuses_stale_epochs()
    {
        string file = Path.Combine(Path.GetTempPath(), $"hex-link-authority-{Guid.NewGuid():N}.json");
        var folder = new HexLinkFolder("shared", Path.GetTempPath());
        var workspace = new HexLinkWorkspace("example", [folder], []);
        string owner = new('A', 64), joiner = new('B', 64);
        var first = new HexLinkAuthority(owner, 1);
        try
        {
            var store = new HexLinkAuthorityStore(file);
            Assert.Null(store.Of(folder));
            Assert.Same(workspace, store.Advertise(workspace));
            Assert.Equal(first, store.Bind(folder, first));
            Assert.Equal(first, new HexLinkAuthorityStore(file).Of(folder));
            Assert.Equal(first, Assert.Single(store.Advertise(workspace).Folders).Authority);
            Assert.Throws<HexLinkException>(() => store.Bind(folder, new HexLinkAuthority(joiner, 1)));

            HexLinkAuthority moved = store.Transfer(folder, first, joiner);
            Assert.Equal(new HexLinkAuthority(joiner, 2), moved);
            Assert.Equal(moved, store.Transfer(folder, first, joiner));
            Assert.Throws<HexLinkException>(() => store.Require(folder, first));
            Assert.Throws<HexLinkException>(() => store.Transfer(folder, first, new string('C', 64)));
            var resumed = new HexLinkAuthorityStore(file);
            resumed.Require(folder, moved);
            Assert.Equal(moved, Assert.Single(resumed.Advertise(workspace).Folders).Authority);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Transfer_checks_Edit_on_the_pinned_old_owner_and_returns_an_authenticated_receipt()
    {
        const HexLinkRights allow = HexLinkRights.View | HexLinkRights.Edit;
        using var pair = await LinkedPair.Up(allow, HexLinkRights.View);
        HexLinkFolder folder = Assert.Single(pair.B.Workspace.Folders);
        var previous = new HexLinkAuthority(pair.B.Device.Id, 1);
        var store = new HexLinkAuthorityStore(Path.Combine(pair.B.Root, "authorities.json"));
        store.Bind(folder, previous);
        pair.B.FolderAuthorities = store;
        pair.B.Workspace = store.Advertise(pair.B.Workspace);
        await pair.PeerB.Hello();

        Assert.Equal(previous, Assert.Single(pair.PeerB.Workspace.Folders).Authority);
        await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerB.TransferAuthority(folder.Name, previous, CancellationToken.None));
        Assert.Equal(previous, store.Of(folder));

        pair.AOnB = allow;
        await pair.PeerB.Hello();
        HexLinkAuthority receipt = await pair.PeerB.TransferAuthority(folder.Name, previous, CancellationToken.None);
        Assert.Equal(new HexLinkAuthority(pair.A.Device.Id, 2), receipt);
        Assert.Equal(receipt, store.Of(folder));
        Assert.Equal(receipt, await pair.PeerB.TransferAuthority(folder.Name, previous, CancellationToken.None));
    }
}
