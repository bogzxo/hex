using System.Text;

using Hex.Core;
using Hex.Core.Git;

namespace Hex.Tests;

public sealed class HexLinkTreeCacheTests
{
    [Fact]
    public void Verified_index_membership_does_not_reuse_an_untracked_path_after_index_changes()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        Run("init", "-q");
        File.WriteAllText(Path.Combine(site.Work, ".gitignore"), "*.txt\n");
        File.WriteAllText(Path.Combine(site.Work, "old.txt"), "tracked");
        File.WriteAllText(Path.Combine(site.Work, "new.txt"), "ignored");
        Run("add", "-f", "old.txt");
        string index = Path.Combine(site.Work, ".git", "index");
        DateTime initialStamp = File.GetLastWriteTimeUtc(index);

        var tree = new HexLinkTree(site.Workspace.Folders[0]);
        tree.Manifest();
        Assert.Equal("new.txt", Assert.Single(tree.Ignored(["old.txt", "new.txt"])));

        Run("update-index", "--force-remove", "old.txt");
        Run("add", "-f", "new.txt");
        File.SetLastWriteTimeUtc(index, initialStamp);
        Assert.Equal("old.txt", Assert.Single(tree.Ignored(["old.txt", "new.txt"])));

        void Run(params string[] args)
        {
            GitResult result = GitProcess.Run(site.Work, args);
            Assert.True(result.Ok, result.Why);
        }
    }

    [Fact]
    public void A_clean_index_reuses_paths_but_never_hides_a_new_untracked_file()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        HexLinkFolder folder = site.Workspace.Folders[0];
        Run("init", "-q");
        File.WriteAllText(Path.Combine(site.Work, "tracked.txt"), "tracked");
        Run("add", "tracked.txt");
        Run("-c", "user.name=Link test", "-c", "user.email=link-test@example.invalid", "commit", "-qm", "tracked");

        HexLinkTree tree = HexLinkTree.Shared(folder, () => HexExcludes.Usual, cacheDirectory: null, streamLarge: true);
        HexLinkManifest first = tree.Manifest();
        Assert.Empty(tree.Ignored(["tracked.txt"]));
        Assert.Equal(first.Files, tree.Manifest(verify: false, reuse: false).Files);
        Assert.Equal(1, tree.ReusedGitListings);

        DateTime stamp = Directory.GetLastWriteTimeUtc(site.Work);
        File.WriteAllText(Path.Combine(site.Work, "untracked.txt"), "new while disconnected");
        Directory.SetLastWriteTimeUtc(site.Work, stamp);
        HexLinkManifest next = tree.Manifest(verify: false, reuse: false);
        Assert.Contains(next.Files, file => file.Path == "work/untracked.txt");
        Assert.Equal(1, tree.ReusedGitListings);

        void Run(params string[] args)
        {
            GitResult result = GitProcess.Run(site.Work, args);
            Assert.True(result.Ok, result.Why);
        }
    }

    [Fact]
    public async Task Explicit_flush_certifies_a_directory_created_during_a_pack()
    {
        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        var tree = new HexLinkTree(site.Workspace.Folders[0]);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<HexLinkTreeChange>();
        using var watch = new HexLinkTreeWatch(tree, batch =>
        {
            foreach (HexLinkTreeChange change in batch)
                changes.Enqueue(change);
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1));
        watch.Seed(tree.Manifest());
        using var publishing = tree.BeginPackPublication();

        string directory = Path.Combine(site.Work, "whole-directory");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kept.txt"), "signed by this scan");
        await watch.Flush();
        Assert.Contains(changes, change => change.Path == "work/whole-directory/kept.txt" && !change.Deleted);
    }

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
    public void Legacy_and_bulk_links_share_one_tree_and_keep_the_legacy_size_limit()
    {
        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        HexLinkFolder folder = site.Workspace.Folders[0];
        using (FileStream stream = File.Create(Path.Combine(folder.Path, "big.bin")))
            stream.SetLength(HexLinkFiles.MOST_READ + 1);

        HexLinkTree legacy = HexLinkTree.Shared(folder, () => HexExcludes.Usual, cacheDirectory: null, streamLarge: false);
        Assert.True(Assert.Single(legacy.Manifest().Files).Big);
        Assert.Same(legacy, HexLinkTree.Shared(folder, () => HexExcludes.Usual, cacheDirectory: null, streamLarge: true));
        HexLinkManifest bulk = legacy.Manifest();
        HexLinkTreeFile file = Assert.Single(bulk.Files);
        Assert.NotNull(file.Hash);
        Assert.True(Assert.Single(HexLinkTree.ForLegacy(bulk).Files).Big);
        Assert.Null(HexLinkTree.ForLegacy(new HexLinkTreeChange(file.Path, file.Hash, Size: file.Size)).Hash);
    }

    [Fact]
    public void Reconnecting_after_the_last_watcher_stops_reads_a_new_manifest()
    {
        using var site = new LinkServices(HexLinkTestKit.DeviceA);
        HexLinkFolder folder = site.Workspace.Folders[0];
        HexLinkTree tree = HexLinkTree.Shared(folder, () => HexExcludes.Usual, cacheDirectory: null, streamLarge: true);
        string file = Path.Combine(folder.Path, "a.txt");
        File.WriteAllText(file, "before");
        HexLinkManifest before;
        using (var first = new HexLinkTreeSubscription(tree, _ => { }, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)))
        {
            before = tree.Manifest();
            first.Seed(before);
        }

        File.WriteAllText(file, "an edit while disconnected");
        using var next = new HexLinkTreeSubscription(tree, _ => { }, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.NotEqual(Assert.Single(before.Files).Hash, Assert.Single(tree.Manifest().Files).Hash);
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
