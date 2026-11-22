using Hex.Core;

using Xunit;

namespace Hex.Tests;

public sealed class HexLinkPrivateStorageTests
{
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "hex-private-storage-tests");

    [Fact]
    public void Portable_debug_profiles_use_the_same_external_home_across_export_order_and_path_spelling()
    {
        string repo = Path.Combine(Temp, "workspace");
        string data = Path.Combine(repo, "Hex", "bin", "Debug", "net11.0", "data", "link");
        string appData = Path.Combine(Temp, "user-data");
        var exports = new[] { new HexLinkFolder("other", Path.Combine(Temp, "other")), new HexLinkFolder("workspace", repo) };

        string? external = HexLinkPrivateStorage.Choose(data, appData, exports);

        Assert.NotNull(external);
        Assert.Equal(Path.Combine(appData, "Hex", "link"), Path.GetDirectoryName(external));
        Assert.Equal(external, HexLinkPrivateStorage.Choose(Path.Combine(data, ".", ""), appData, [.. exports.Reverse()]));
        Assert.NotEqual(external, HexLinkPrivateStorage.Choose(Path.Combine(data, "another-profile"), appData, exports));
    }

    [Fact]
    public void Unshared_profile_keeps_its_existing_private_store()
    {
        string data = Path.Combine(Temp, "profile", "data", "link");
        var exports = new[] { new HexLinkFolder("other", Path.Combine(Temp, "work")) };
        Assert.Equal(Path.Combine(data, "wt"), HexLinkPrivateStorage.Choose(data, Path.Combine(Temp, "user"), exports));
    }

    [Fact]
    public void Existing_external_journal_stays_selected_after_an_export_is_removed()
    {
        string root = Path.Combine(Temp, Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string data = Path.Combine(repo, "data", "link");
        string appData = Path.Combine(root, "user");
        string external = HexLinkPrivateStorage.Choose(data, appData, [new("repo", repo)])!;
        Directory.CreateDirectory(external);
        try
        {
            Assert.Equal(external, HexLinkPrivateStorage.Choose(data, appData, []));
        }
        finally
        {
            Directory.Delete(external);
        }
    }

    [Fact]
    public void Both_locations_are_refused_if_any_export_overlaps_them()
    {
        string repo = Path.Combine(Temp, "repo");
        string data = Path.Combine(repo, "data", "link");
        string appData = Path.Combine(Temp, "user");
        var workspace = new HexLinkFolder("repo", repo);
        string external = HexLinkPrivateStorage.Choose(data, appData, [workspace])!;

        Assert.Null(HexLinkPrivateStorage.Choose(data, appData, [workspace, new HexLinkFolder("user", appData)]));
        Assert.Null(HexLinkPrivateStorage.Choose(data, appData,
            [workspace, new HexLinkFolder("private child", Path.Combine(external, "drafts"))]));
        Assert.Null(HexLinkPrivateStorage.Choose(data, "relative", [workspace]));
    }
}
