using Hex.Core;
using Hex.Core.Git;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed partial class HexLinkSyncTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_alternates_warning_keeps_a_complete_git_listing(bool rootRepository)
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        string scope = rootRepository ? "" : "nested";
        string left = Path.Combine(pair.A.Work, scope), right = Path.Combine(pair.B.Work, scope);
        MakeSharedCheckout(left, right);
        string alternates = Path.Combine(right, ".git", "objects", "info", "alternates");
        File.WriteAllText(alternates, Path.Combine(pair.B.Root, "missing-objects").Replace('\\', '/') + "\n");
        GitResult listed = GitProcess.Run(right, ["ls-files", "-co", "--exclude-standard", "-z"]);
        Assert.True(listed.Ok, listed.Why);
        Assert.Contains("kept.txt\0", listed.Output, StringComparison.Ordinal);
        // Git builds differ in whether this missing alternate produces a diagnostic.
        bool reportsAlternate = listed.Error.Contains("alternate", StringComparison.OrdinalIgnoreCase);

        string relative = rootRepository ? "added.txt" : "nested/added.txt";
        Put(pair.B, relative, "from the checkout with the warning");
        using HexLinkSync sync = SyncOf(pair);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await sync.Start(cancel: limit.Token);
        await Settled(sync);

        Assert.Equal("from the checkout with the warning", File.ReadAllText(Path.Combine(pair.A.Work, relative)));
        if (reportsAlternate)
        {
            Assert.Contains("Git warning", sync.Summary, StringComparison.Ordinal);
            Assert.Contains("alternate", sync.Summary, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty(sync.MissingRepositories.Unavailable!);
    }

    [Fact]
    public async Task A_broken_nested_listing_quarantines_a_previously_synced_subtree()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var pair = await LinkedPair.Up(EDIT, EDIT);
        string left = Path.Combine(pair.A.Work, "nested"), right = Path.Combine(pair.B.Work, "nested");
        MakeSharedCheckout(left, right);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (HexLinkSync first = SyncOf(pair))
        {
            await first.Start(cancel: limit.Token);
            await Settled(first);
            await first.Flush(limit.Token);

            File.WriteAllText(Path.Combine(right, ".git", "index"), "not an index");
            Assert.False(GitProcess.Run(right, ["ls-files", "-co", "--exclude-standard", "-z"]).Ok);
            File.Delete(Path.Combine(right, "kept.txt"));
            Put(pair.B, "nested/only-there.txt", "stay here");
            // A watcher already holding the old listing must not report the omitted subtree as deletions.
            await first.Flush(limit.Token);
            Assert.True(File.Exists(Path.Combine(left, "kept.txt")));
            Put(pair.A, "nested/only-here.txt", "stay here too");
            await first.StopForRepositoryRefresh().WaitAsync(limit.Token);
        }

        Put(pair.B, "sibling.txt", "keep syncing outside the failed repo");
        using HexLinkSync sync = SyncOf(pair);
        await sync.Start(cancel: limit.Token);
        await Settled(sync);
        await sync.Flush(limit.Token);

        Assert.Equal("keep syncing outside the failed repo", File.ReadAllText(Path.Combine(pair.A.Work, "sibling.txt")));
        Assert.True(File.Exists(Path.Combine(left, "kept.txt")));
        Assert.False(File.Exists(Path.Combine(left, "only-there.txt")));
        Assert.False(File.Exists(Path.Combine(right, "only-here.txt")));
        Assert.False(File.Exists(Path.Combine(right, "kept.txt")));
        Assert.Equal("nested", Assert.Single(sync.MissingRepositories.Unavailable!));
        Assert.Empty(sync.MissingRepositories.There);
        Assert.Contains("Git skipped nested", sync.Summary, StringComparison.Ordinal);
        Assert.False(await pair.PeerB.ConfirmAbsent("work", "work/nested/kept.txt", limit.Token));
    }

    private static void MakeSharedCheckout(string left, string right)
    {
        Directory.CreateDirectory(left);
        RunGit(left, "init", "-q");
        File.WriteAllText(Path.Combine(left, "kept.txt"), "trusted base");
        RunGit(left, "add", "kept.txt");
        RunGit(left, "-c", "user.name=Link test", "-c", "user.email=link-test@example.invalid", "commit", "-qm", "initial");
        RunGit(Path.GetDirectoryName(right)!, "clone", "-q", left, right);

        static void RunGit(string directory, params string[] args)
        {
            GitResult result = GitProcess.Run(directory, args, patience: TimeSpan.FromSeconds(15));
            Assert.True(result.Ok, result.Why);
        }
    }
}
