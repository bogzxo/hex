using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>Which claims stop an agent's write: another device's do, this device's own editor's never do.</summary>
public class HexLinkClaimGuardTests
{
    private static HexLinkClaim Claim(HexLinkDevice device, int? chat, HexLinkClaimKind kind) => new(
        new HexLinkClaimScope("work", "scope"), device, new HexLinkClaimDraft(new HexLinkClaimKey("a.txt", false, chat, kind), "bogdan"), 0, 35_000, string.Empty);

    [Fact]
    public void A_devices_own_editor_claim_does_not_block_its_own_agent()
    {
        HexLinkDevice me = HexLinkTestKit.DeviceA, other = HexLinkTestKit.DeviceB;
        Func<HexLinkClaim, bool> blocks = DawnScene.LinkClaimBlocks(me.Id, 7);

        Assert.False(blocks(Claim(me, null, HexLinkClaimKind.Editing)));
        Assert.False(blocks(Claim(me, 7, HexLinkClaimKind.Planned)));
        Assert.True(blocks(Claim(me, 9, HexLinkClaimKind.Planned)));
        Assert.True(blocks(Claim(other, null, HexLinkClaimKind.Editing)));
        Assert.True(blocks(Claim(other, 7, HexLinkClaimKind.Planned)));
        Assert.False(DawnScene.LinkClaimBlocks(me.Id, null)(Claim(me, null, HexLinkClaimKind.Editing)));
    }
}
