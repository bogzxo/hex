using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

public sealed class HexLinkSyncOwnerTests
{
    [Fact]
    public void Matching_pins_give_the_bound_folder_one_sync_owner()
    {
        string owner = HexLinkTestKit.DeviceA.Id;
        string follower = HexLinkTestKit.DeviceB.Id;
        var pin = new HexLinkAuthority(owner, 2);

        Assert.Null(DawnScene.LinkBoundSyncOwner(owner, null, null));
        Assert.True(DawnScene.LinkBoundSyncOwner(owner, pin, pin));
        Assert.False(DawnScene.LinkBoundSyncOwner(follower, pin, pin));
        Assert.False(DawnScene.LinkBoundSyncOwner(owner, pin, pin with { Epoch = 1 }));
        Assert.False(DawnScene.LinkBoundSyncOwner(owner, pin, pin with { DeviceId = follower }));
        Assert.False(DawnScene.LinkBoundSyncOwner(owner, null, pin));
        Assert.False(DawnScene.LinkBoundSyncOwner(owner, pin, null));
    }
}
