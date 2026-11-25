using Hex.Dawn;

namespace Hex.Tests;

public sealed class LinkRemoteHoldMapTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "hex-remote-holds", "work");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "hex-remote-holds", "docs");

    [Fact]
    public void A_held_wire_becomes_the_file_under_its_folder_with_the_platform_separator()
    {
        var map = DawnScene.LinkRemoteHoldMap([new("mac", "Mac", "work", Root, ["work/src/a.txt", "work/b.txt"])]);

        Assert.Equal(2, map.Count);
        Assert.Equal(new DawnScene.LinkRemoteHolder("mac", "Mac"),
            map[Path.Combine(Root, "src", "a.txt")]);
        Assert.True(map.ContainsKey(Path.Combine(Root, "b.txt")));
    }

    [Fact]
    public void Two_folders_are_each_mapped_under_their_own_root()
    {
        var map = DawnScene.LinkRemoteHoldMap(
        [
            new("mac", "Mac", "work", Root, ["work/a.txt"]),
            new("pc", "PC", "docs", Other, ["docs/notes/n.md"]),
        ]);

        Assert.Equal("Mac", map[Path.Combine(Root, "a.txt")].Name);
        Assert.Equal("PC", map[Path.Combine(Other, "notes", "n.md")].Name);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void A_wire_of_another_folder_or_with_no_file_is_ignored()
    {
        var map = DawnScene.LinkRemoteHoldMap([new("mac", "Mac", "work", Root, ["docs/a.txt", "work", "work/", "/a.txt", "work/ok.txt"])]);

        Assert.Equal(Path.Combine(Root, "ok.txt"), Assert.Single(map).Key);
    }

    [Fact]
    public void The_first_source_keeps_a_file_two_devices_hold()
    {
        var map = DawnScene.LinkRemoteHoldMap(
        [
            new("mac", "Mac", "work", Root, ["work/a.txt"]),
            new("pc", "PC", "work", Root, ["work/a.txt"]),
        ]);

        Assert.Equal("mac", Assert.Single(map).Value.DeviceId);
    }

    [Fact]
    public void Nothing_held_is_an_empty_map()
    {
        Assert.Empty(DawnScene.LinkRemoteHoldMap([]));
        Assert.Empty(DawnScene.LinkRemoteHoldMap([new("mac", "Mac", "work", Root, [])]));
    }
}
