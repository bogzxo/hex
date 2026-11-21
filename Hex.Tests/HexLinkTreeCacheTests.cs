using System.Text;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkTreeCacheTests
{
    [Fact]
    public void Persisted_tree_hashes_are_reused_and_a_verification_reads_same_stamp_changes()
    {
        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        HexLinkFolder folder = site.Workspace.Folders[0];
        string cache = Path.Combine(site.Root, "cache");
        string changed = Path.Combine(folder.Path, "0.txt");
        for (int i = 0; i < 64; i++)
            File.WriteAllText(Path.Combine(folder.Path, $"{i}.txt"), $"original {i}\n");

        var cold = new HexLinkTree(folder, cacheDirectory: cache);
        HexLinkManifest first = cold.Manifest();
        Assert.Equal(64, first.Files.Count);
        Assert.Equal(64, cold.ComputedHashes);

        var warm = new HexLinkTree(folder, cacheDirectory: cache);
        Assert.Equal(first.Files, warm.Manifest().Files);
        Assert.Equal(0, warm.ComputedHashes);

        DateTime timestamp = File.GetLastWriteTimeUtc(changed);
        File.WriteAllText(changed, "modified 0\n");
        File.SetLastWriteTimeUtc(changed, timestamp);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(changed));

        HexLinkManifest verified = warm.Manifest(verify: true);
        HexLinkTreeFile file = Assert.Single(verified.Files, each => each.Path == folder.Name + "/0.txt");
        Assert.Equal(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("modified 0\n")), file.Hash);
        Assert.Equal(64, warm.ComputedHashes);
        Assert.NotEqual(Assert.Single(first.Files, each => each.Path == file.Path).Hash, file.Hash);
    }
}
