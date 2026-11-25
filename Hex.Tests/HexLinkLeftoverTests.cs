using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>Leftover Hex Link issues: duplicate device names, idle editing claims, empty name-matched folders.</summary>
public sealed class HexLinkLeftoverTests
{
    private const string A = "3F9A2C11AAAAAAAA", B = "7B01D4E2BBBBBBBB";

    [Fact]
    public void A_name_nobody_else_has_stays_plain()
    {
        Assert.Equal("BOGZPC", HexLinkNames.Distinct("BOGZPC", A, [("Studio Mac", B)]));
        Assert.Equal("BOGZPC", HexLinkNames.Distinct("BOGZPC", A, []));
    }

    [Fact]
    public void Two_devices_with_one_name_get_their_id_tail()
    {
        Assert.Equal("BOGZPC · 3F9A2C", HexLinkNames.Distinct("BOGZPC", A, [("bogzpc", B)]));
        Assert.Equal("BOGZPC · 7B01D4", HexLinkNames.Distinct("BOGZPC", B, [("BOGZPC", A)]));
    }

    [Fact]
    public void The_same_device_heard_twice_or_by_an_id_prefix_is_not_a_clash()
    {
        Assert.Equal("BOGZPC", HexLinkNames.Distinct("BOGZPC", A, [("BOGZPC", A), ("BOGZPC", A[..8])]));
    }

    [Fact]
    public void An_editing_claim_from_an_mcp_edit_lapses_after_a_lease_of_idle()
    {
        long stamp = 100_000, edit = 100_400, lease = HexLinkClaims.LEASE_MS;
        Assert.False(DawnScene.LinkEditingClaimLapsed(stamp, edit, edit + lease - 1));
        Assert.True(DawnScene.LinkEditingClaimLapsed(stamp, edit, edit + lease));
    }

    [Fact]
    public void A_claim_of_a_person_typing_never_lapses_by_idle()
    {
        long edit = 200_000;
        Assert.False(DawnScene.LinkEditingClaimLapsed(100_000, edit, edit + 10 * HexLinkClaims.LEASE_MS));
        Assert.False(DawnScene.LinkEditingClaimLapsed(0, edit, edit + 10 * HexLinkClaims.LEASE_MS));
    }

    [Fact]
    public void Only_a_folder_with_nothing_in_it_is_empty()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-link-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.True(DawnScene.LinkFolderIsEmpty(folder));
            File.WriteAllText(Path.Combine(folder, "a.txt"), "x");
            Assert.False(DawnScene.LinkFolderIsEmpty(folder));
            Assert.False(DawnScene.LinkFolderIsEmpty(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
