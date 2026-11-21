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
}
