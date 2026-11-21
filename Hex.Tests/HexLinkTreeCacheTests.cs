using System.Text;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkTreeCacheTests
{
    [Fact]
    public async Task Shared_watchers_fan_out_changes_and_catch_up_new_subscribers()
    {
        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        HexLinkFolder folder = site.Workspace.Folders[0];
        string cache = Path.Combine(site.Root, "cache");
        HexLinkTree tree = HexLinkTree.Shared(folder, () => HexExcludes.Usual, cache, streamLarge: true);
        Assert.Same(tree, HexLinkTree.Shared(folder, () => HexExcludes.Usual, cache, streamLarge: true));
        string file = Path.Combine(folder.Path, "a.txt");
        File.WriteAllText(file, "before");
        HexLinkManifest before = tree.Manifest();

        var first = new System.Collections.Concurrent.ConcurrentQueue<HexLinkTreeChange>();
        var second = new System.Collections.Concurrent.ConcurrentQueue<HexLinkTreeChange>();
        using var one = new HexLinkTreeSubscription(tree, batch =>
        {
            foreach (HexLinkTreeChange change in batch)
                first.Enqueue(change);
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        one.Seed(before);
        using var two = new HexLinkTreeSubscription(tree, batch =>
        {
            foreach (HexLinkTreeChange change in batch)
                second.Enqueue(change);
        }, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        File.WriteAllText(file, "after");
        await one.Flush();
        Assert.Equal("work/a.txt", Assert.Single(first).Path);
        Assert.Empty(second);
        two.Seed(before);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void Windows_persists_the_received_executable_bit_in_the_tree_cache()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        HexLinkFolder folder = site.Workspace.Folders[0];
        string cache = Path.Combine(site.Root, "cache");
        string file = Path.Combine(folder.Path, "script");
        File.WriteAllText(file, "echo hello");
        var first = new HexLinkTree(folder, cacheDirectory: cache);
        first.RememberMode(file, true);
        Assert.True(Assert.Single(first.Manifest().Files).Executable);
        var second = new HexLinkTree(folder, cacheDirectory: cache);
        Assert.True(Assert.Single(second.Manifest().Files).Executable);
        Assert.Equal(0, second.ComputedHashes);
    }

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
