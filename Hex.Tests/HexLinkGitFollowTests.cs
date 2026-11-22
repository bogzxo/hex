using Hex.Core;
using Hex.Core.Git;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

internal sealed partial class LinkServices : IHexLinkGitFollowing
{
    public string GitFollowData => Root;
    public bool FollowGit { get; set; }
    public bool GitFollows(HexLinkFolder folder, HexLinkDevice peer) => FollowGit;
}

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

    [Fact]
    public async Task Recovers_the_prepared_index_after_a_restart_between_ref_and_index_commit()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var checkout = new LinkServices(DeviceA);
        string root = checkout.Work, gitDirectory = Path.Combine(root, ".git");
        string index = Path.Combine(gitDirectory, "index");
        HexLinkHeadTip original = Setup(root);
        byte[] oldIndex = File.ReadAllBytes(index);
        HexLinkHeadTip target = Commit(root, "later");
        byte[] prepared = File.ReadAllBytes(index);
        File.WriteAllBytes(index + ".lock", prepared);
        File.WriteAllBytes(index, oldIndex);
        string journal = Path.Combine(gitDirectory, HexLinkGitFollow.JOURNAL);
        File.WriteAllText(journal, new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["old"] = original.ToJson(), ["next"] = target.ToJson(),
            ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prepared))
        }.ToJsonString());

        var scan = HexLinkGitFollowScan.For(root);
        Assert.Null(scan.Read(CancellationToken.None));
        Assert.Equal(target, await scan.ReadStable(CancellationToken.None));
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(index + ".lock"));
        Assert.Equal("later", Git(root, "show", ":main.txt"));
        Assert.Equal(string.Empty, Git(root, "status", "--porcelain"));
    }

    [Fact]
    public async Task Synchronizes_git_and_files_across_root_nested_and_submodule_checkouts()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var pair = await LinkedPair.Up(Edit, Edit);
        await Task.Run(() =>
        {
            Setup(pair.A.Work);
            Setup(PathForRepo(pair.A, "nested"));
            HexLinkHeadTip module = Setup(PathForRepo(pair.A, "module"));
            File.WriteAllText(Path.Combine(pair.A.Work, ".gitmodules"),
                "[submodule \"module\"]\n\tpath = module\n\turl = https://invalid.example/module.git\n");
            Git(pair.A.Work, "add", ".gitmodules");
            Git(pair.A.Work, "update-index", "--add", "--cacheinfo", $"160000,{module.Head},module");
            Git(pair.A.Work, "commit", "-qm", "add submodule");
            Git(pair.B.Root, "clone", "--quiet", "--no-local", pair.A.Work, pair.B.Work);
            Git(pair.B.Root, "clone", "--quiet", "--no-local", PathForRepo(pair.A, "nested"), PathForRepo(pair.B, "nested"));
            Git(pair.B.Root, "clone", "--quiet", "--no-local", PathForRepo(pair.A, "module"), PathForRepo(pair.B, "module"));
            Configure(pair.B.Work);
        });
        pair.A.FollowGit = pair.B.FollowGit = true;
        using var sync = Follows(pair.A, pair.PeerB);
        await sync.Start().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(HexLinkSyncState.Idle, sync.State);

        HexLinkHeadTip moduleTarget = Commit(PathForRepo(pair.A, "module"), "submodule advanced");
        HexLinkHeadTip nestedTarget = Commit(PathForRepo(pair.A, "nested"), "nested advanced");
        Git(pair.A.Work, "update-index", "--cacheinfo", $"160000,{moduleTarget.Head},module");
        Git(pair.A.Work, "commit", "-qm", "advance gitlink");
        HexLinkHeadTip rootTarget = Tip(pair.A.Work);
        await WaitFor(() => At(pair.B.Work, rootTarget)
            && At(PathForRepo(pair.B, "nested"), nestedTarget) && At(PathForRepo(pair.B, "module"), moduleTarget), sync);
        await WaitFor(() => File.ReadAllText(Path.Combine(pair.B.Work, "nested", "main.txt")) == "nested advanced"
            && File.ReadAllText(Path.Combine(pair.B.Work, "module", "main.txt")) == "submodule advanced", sync);
        Assert.Contains(moduleTarget.Head, Git(pair.B.Work, "ls-files", "--stage", "module"));

        File.WriteAllText(Path.Combine(pair.A.Work, "aside.txt"), "my unrelated dirt");
        Git(pair.B.Work, "checkout", "-qb", "topic");
        HexLinkHeadTip fromB = Commit(pair.B.Work, "edit on a new branch");
        await WaitFor(() => At(pair.A.Work, fromB) && File.ReadAllText(Path.Combine(pair.A.Work, "main.txt")) == "edit on a new branch", sync);
        Assert.Equal("my unrelated dirt", File.ReadAllText(Path.Combine(pair.A.Work, "aside.txt")));
        Assert.Contains("aside.txt", Git(pair.A.Work, "status", "--porcelain"));

        Git(pair.A.Work, "checkout", "-q", "development");
        await WaitFor(() => At(pair.B.Work, rootTarget)
            && File.ReadAllText(Path.Combine(pair.B.Work, "main.txt")) == "the first version", sync);
        Assert.Equal(fromB.Head, Git(pair.B.Work, "rev-parse", "refs/heads/topic"));
    }

    [Fact]
    public async Task Catches_up_offline_checks_and_audits_amend_but_keeps_both_diverging_tips()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var pair = await LinkedPair.Up(Edit, Edit);
        Setup(pair.A.Work);
        Git(pair.B.Root, "clone", "--quiet", "--no-local", pair.A.Work, pair.B.Work);
        Configure(pair.B.Work);
        pair.A.FollowGit = pair.B.FollowGit = true;
        using (var first = Follows(pair.A, pair.PeerB))
            await first.Start().WaitAsync(TimeSpan.FromMinutes(2));

        pair.PeerA.Session.Dispose();
        Assert.True(Eventually(() => pair.LinkA.Peers.Count == 0 && pair.LinkB.Peers.Count == 0, 30_000));
        Commit(pair.B.Work, "first offline change");
        HexLinkHeadTip online = Commit(pair.B.Work, "last offline change");
        HexLinkPeer reconnected = await Connect(pair.LinkA, pair.A, pair.LinkB, pair.B);
        using var sync = Follows(pair.A, reconnected);
        await sync.Start().WaitAsync(TimeSpan.FromMinutes(2));
        await WaitFor(() => At(pair.A.Work, online)
            && File.ReadAllText(Path.Combine(pair.A.Work, "main.txt")) == "last offline change", sync);

        File.WriteAllText(Path.Combine(pair.A.Work, "main.txt"), "amended file");
        Git(pair.A.Work, "commit", "-qa", "--amend", "-m", "amended");
        HexLinkHeadTip amend = Tip(pair.A.Work);
        await WaitFor(() => At(pair.B.Work, amend)
            && File.ReadAllText(Path.Combine(pair.B.Work, "main.txt")) == "amended file", sync);
        Assert.Contains(online.Head, Git(pair.B.Work, "for-each-ref", "--format=%(objectname)", "refs/hexlink/backup/"));
        Assert.Contains(pair.B.Audited, entry => entry.Detail.Contains("(rewrite)", StringComparison.Ordinal));

        // Hold on to the old agreement while both independently make an unreachable commit.
        sync.Dispose();
        HexLinkHeadTip here = Commit(pair.A.Work, "independent desk");
        HexLinkHeadTip there = Commit(pair.B.Work, "independent laptop");
        using var divergent = Follows(pair.A, reconnected);
        await divergent.Start().WaitAsync(TimeSpan.FromMinutes(2));
        await WaitFor(() => divergent.GitFollowIssue?.Contains("has commits", StringComparison.Ordinal) == true, divergent);
        Assert.True(At(pair.A.Work, here) && At(pair.B.Work, there));
        Assert.NotEqual(HexLinkSyncState.NeedsGit, divergent.State);
        File.WriteAllText(Path.Combine(pair.B.Work, "unrelated-new.txt"), "ordinary sync stays live");
        await WaitFor(() => File.Exists(Path.Combine(pair.A.Work, "unrelated-new.txt")), divergent);
    }

    [Fact]
    public async Task A_local_commit_follows_without_waiting_for_the_full_check()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var pair = await LinkedPair.Up(Edit, Edit);
        Setup(pair.A.Work);
        Git(pair.B.Root, "clone", "--quiet", "--no-local", pair.A.Work, pair.B.Work);
        Configure(pair.B.Work);
        pair.A.FollowGit = pair.B.FollowGit = true;
        // The full check, which asks the other side for its tips, never comes during the test: only this side's HEAD
        // read from its files can notice the commit
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "sync"))
        {
            GitFollowEnabled = static () => true, GitFollowData = pair.A.GitFollowData,
            Quiet = TimeSpan.FromMilliseconds(70), Rescan = TimeSpan.FromSeconds(2), GitFollowFullCheck = TimeSpan.FromHours(1)
        };
        await sync.Start().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(HexLinkSyncState.Idle, sync.State);

        HexLinkHeadTip tip = Commit(pair.A.Work, "made here");
        await WaitFor(() => At(pair.B.Work, tip) && File.ReadAllText(Path.Combine(pair.B.Work, "main.txt")) == "made here", sync);
    }

    [Fact]
    public async Task A_commit_made_on_one_worker_follows_through_the_pinned_authority_to_a_second_worker()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var pair = await LinkedPair.Up(Edit, Edit);
        using var third = new LinkServices(DeviceC) { FollowGit = true };
        using var thirdLink = new HexLink(third);
        Setup(pair.A.Work);
        Git(pair.B.Root, "clone", "--quiet", "--no-local", pair.A.Work, pair.B.Work);
        Git(third.Root, "clone", "--quiet", "--no-local", pair.A.Work, third.Work);
        Configure(pair.B.Work);
        var pin = new HexLinkAuthority(DeviceA.Id, 1);
        foreach (LinkServices services in new[] { pair.A, pair.B, third })
        {
            services.FollowGit = true;
            services.Workspace = services.Workspace with { Folders = [services.Workspace.Folders[0] with { Authority = pin }] };
        }

        HexLinkPeer peerC = await Connect(pair.LinkA, pair.A, thirdLink, third);
        using var workerOne = Follows(pair.A, pair.PeerB);
        using var workerTwo = Follows(pair.A, peerC);
        await Task.WhenAll(workerOne.Start(), workerTwo.Start()).WaitAsync(TimeSpan.FromMinutes(2));
        HexLinkHeadTip tip = Commit(pair.B.Work, "made on the first worker");
        await WaitFor(() => At(pair.A.Work, tip) && At(third.Work, tip)
            && File.ReadAllText(Path.Combine(third.Work, "main.txt")) == "made on the first worker", workerTwo);
        Assert.Single(thirdLink.Peers);
        Assert.Equal(DeviceA.Id, thirdLink.Peers[0].Device.Id);
    }

    private static async Task<HexLinkPeer> Connect(HexLink left, LinkServices leftServices, HexLink right, LinkServices rightServices)
    {
        var (a, b) = Pair();
        Task<HexLinkPeer> remote = left.Add(new HexLinkConnection(a, rightServices.Device, "memory", true, () => Edit));
        Task<HexLinkPeer> local = right.Add(new HexLinkConnection(b, leftServices.Device, "memory", false, () => Edit));
        await Task.WhenAll(remote, local).WaitAsync(TimeSpan.FromSeconds(30));
        return await remote;
    }

    private const HexLinkRights Edit = HexLinkRights.View | HexLinkRights.Edit;

    private static HexLinkSync Follows(LinkServices local, HexLinkPeer peer) =>
        new(peer, local.Workspace.Folders[0], Path.Combine(local.Root, "sync"))
        {
            GitFollowEnabled = static () => true, GitFollowData = local.GitFollowData,
            Quiet = TimeSpan.FromMilliseconds(70), Rescan = TimeSpan.FromSeconds(2)
        };

    private static async Task WaitFor(Func<bool> matched, HexLinkSync sync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            while (!matched())
                await Task.Delay(75, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.True(matched(), sync.Summary);
        }
    }

    private static bool At(string repo, HexLinkHeadTip tip) =>
        HexLinkGitFollowScan.For(repo).Read(CancellationToken.None) == tip;

    private static HexLinkHeadTip Commit(string root, string text)
    {
        File.WriteAllText(Path.Combine(root, "main.txt"), text);
        Git(root, "commit", "-qam", text);
        return Tip(root);
    }

    private static string PathForRepo(LinkServices services, string sub)
    {
        string path = Path.Combine(services.Work, sub);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Configure(string root)
    {
        Git(root, "config", "user.name", "Link Test");
        Git(root, "config", "user.email", "link-test@example.test");
    }

    private static HexLinkHeadTip Setup(string root)
    {
        Git(root, "init", "-q", "-b", "development");
        Configure(root);
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
