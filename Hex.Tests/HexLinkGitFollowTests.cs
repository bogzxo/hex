using Hex.Core;
using Hex.Core.Git;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkGitFollowTests
{
    [Fact]
    public async Task Advances_multiple_commits_and_rebuilds_the_index_without_writing_working_files()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var checkout = new LinkServices(DeviceA);
        string root = checkout.Work;
        HexLinkHeadTip initial = Setup(root);
        string main = Path.Combine(root, "main.txt"), aside = Path.Combine(root, "aside.txt");
        File.WriteAllText(main, "the second version");
        Git(root, "commit", "-qam", "second");
        File.WriteAllText(main, "the third version");
        Git(root, "commit", "-qam", "third");
        HexLinkHeadTip leader = Tip(root);
        Git(root, "reset", "--hard", initial.Head);
        File.WriteAllText(aside, "stash me");
        Git(root, "stash", "push", "-qm", "untouched");
        string stash = Git(root, "rev-parse", "refs/stash");
        File.WriteAllText(aside, "my local edit");
        Git(root, "remote", "add", "origin", "https://invalid.example/existing");
        Git(root, "config", "branch.development.remote", "origin");
        Git(root, "config", "branch.development.merge", "refs/heads/original");
        string hooks = Path.Combine(root, ".git", "hooks-fixture");
        Directory.CreateDirectory(hooks);
        string hook = Path.Combine(hooks, "reference-transaction");
        File.WriteAllText(hook, "#!/bin/sh\ntouch hook-ran\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Git(root, "config", "core.hooksPath", hooks);

        HexLinkHeadMove moved = await HexLinkGitFollow.Move(root, initial, leader, initial, CancellationToken.None);
        Assert.Null(moved.Why);
        Assert.False(moved.Rewrite);
        Assert.Equal(leader, Tip(root));
        Assert.Equal(initial.Head, Git(root, "rev-parse", moved.Backup!));
        Assert.Equal("the third version", Git(root, "show", ":main.txt"));
        Assert.Equal("the first version", File.ReadAllText(main));
        Assert.Equal("my local edit", File.ReadAllText(aside));
        Assert.Equal(stash, Git(root, "rev-parse", "refs/stash"));
        Assert.Equal("https://invalid.example/existing", Git(root, "remote", "get-url", "origin"));
        Assert.Equal("refs/heads/original", Git(root, "config", "branch.development.merge"));
        Assert.False(File.Exists(Path.Combine(root, "hook-ran")));
    }

    [Fact]
    public async Task Switches_to_existing_new_and_detached_branches_without_checking_out_files()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var checkout = new LinkServices(DeviceA);
        string root = checkout.Work, main = Path.Combine(root, "main.txt");
        HexLinkHeadTip initial = Setup(root);
        Git(root, "checkout", "-qb", "topic");
        File.WriteAllText(main, "on topic");
        Git(root, "commit", "-qam", "topic change");
        HexLinkHeadTip topic = Tip(root);
        Git(root, "checkout", "-q", initial.Branch!);

        Assert.Null((await HexLinkGitFollow.Move(root, initial, topic, initial, CancellationToken.None)).Why);
        Assert.Equal(topic, Tip(root));
        Assert.Equal("the first version", File.ReadAllText(main));
        HexLinkHeadTip created = topic with { Branch = "new-branch" };
        Assert.Null((await HexLinkGitFollow.Move(root, topic, created, topic, CancellationToken.None)).Why);
        Assert.Equal(created, Tip(root));
        HexLinkHeadTip detached = new(initial.Head, null);
        Assert.Null((await HexLinkGitFollow.Move(root, created, detached, created, CancellationToken.None)).Why);
        Assert.Equal(detached, Tip(root));
        Assert.Null((await HexLinkGitFollow.Move(root, detached, topic, detached, CancellationToken.None)).Why);
        Assert.Equal(topic, Tip(root));
        Assert.Equal("the first version", File.ReadAllText(main));
    }

    [Fact]
    public async Task Refuses_divergence_and_allows_only_a_previously_agreed_rewrite_with_a_backup()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var checkout = new LinkServices(DeviceA);
        string root = checkout.Work, main = Path.Combine(root, "main.txt");
        HexLinkHeadTip first = Setup(root);
        File.WriteAllText(main, "local commit");
        Git(root, "commit", "-qam", "local");
        HexLinkHeadTip local = Tip(root);
        Git(root, "checkout", "-qb", "other-tip", first.Head);
        File.WriteAllText(main, "remote replacement");
        Git(root, "commit", "-qam", "replacement");
        HexLinkHeadTip remote = Tip(root) with { Branch = local.Branch };
        Git(root, "checkout", "-q", local.Branch!);

        HexLinkHeadMove refused = await HexLinkGitFollow.Move(root, local, remote, first, CancellationToken.None);
        Assert.Contains("commits", refused.Why);
        Assert.Equal(local, Tip(root));

        HexLinkHeadMove amended = await HexLinkGitFollow.Move(root, local, remote, local, CancellationToken.None);
        Assert.Null(amended.Why);
        Assert.True(amended.Rewrite);
        Assert.Equal(remote, Tip(root));
        Assert.Equal(local.Head, Git(root, "rev-parse", amended.Backup!));
        Assert.Equal("local commit", File.ReadAllText(main));
    }

    [Fact]
    public async Task Does_not_drop_staged_content()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var checkout = new LinkServices(DeviceA);
        string root = checkout.Work, main = Path.Combine(root, "main.txt");
        HexLinkHeadTip initial = Setup(root);
        File.WriteAllText(main, "next");
        Git(root, "commit", "-qam", "next");
        HexLinkHeadTip target = Tip(root);
        Git(root, "reset", "--hard", initial.Head);
        File.WriteAllText(main, "only copy in the index");
        Git(root, "add", "main.txt");
        File.WriteAllText(main, "different working bytes");

        HexLinkHeadMove refused = await HexLinkGitFollow.Move(root, initial, target, initial, CancellationToken.None);
        Assert.Contains("staged changes", refused.Why);
        Assert.Equal(initial, Tip(root));
        Assert.Equal("only copy in the index", Git(root, "show", ":main.txt"));
        Assert.Equal("different working bytes", File.ReadAllText(main));
    }

    private static HexLinkHeadTip Setup(string root)
    {
        Git(root, "init", "-q", "-b", "development");
        Git(root, "config", "user.name", "Link Test");
        Git(root, "config", "user.email", "link-test@example.test");
        File.WriteAllText(Path.Combine(root, "main.txt"), "the first version");
        File.WriteAllText(Path.Combine(root, "aside.txt"), "baseline aside");
        Git(root, "add", ".");
        Git(root, "commit", "-qm", "first");
        return Tip(root);
    }

    private static HexLinkHeadTip Tip(string root) => HexLinkGitFollow.Read(root, CancellationToken.None);

    private static string Git(string root, params string[] args)
    {
        GitResult git = GitProcess.Run(root, args, patience: TimeSpan.FromMinutes(2));
        Assert.True(git.Ok, git.Why);
        return git.Output.Trim();
    }
}
