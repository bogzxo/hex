using Hex.Core;
using Hex.Core.Git;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkProvisionTests
{
    [Fact]
    public async Task Alignment_imports_the_hosts_unpushed_nested_commit_and_preserves_the_origin()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string host = pair.A.Work, local = pair.B.Work;
        string hostNested = Path.Combine(host, "nested"), localNested = Path.Combine(local, "nested");
        MakeRepo(host, "/nested/\n");
        MakeRepo(hostNested);
        Git(hostNested, "remote", "add", "origin", "https://invalid.example/real/nested.git");
        Git(pair.B.Root, "clone", "-q", host, local);
        Git(local, "clone", "-q", hostNested, localNested);
        string ancestor = Head(localNested);
        string origin = Git(localNested, "remote", "get-url", "origin");
        File.WriteAllText(Path.Combine(hostNested, "tracked.txt"), "on host only\n");
        Git(hostNested, "commit", "-qam", "Only on host");
        Git(host, "commit", "--allow-empty", "-qm", "Root also moved");

        await HexLinkProvision.Align(pair.PeerA, pair.B.Workspace.Folders[0], null, CancellationToken.None);

        Assert.Equal(Head(host), Head(local));
        Assert.Equal(Head(hostNested), Head(localNested));
        Assert.Equal(ancestor, Git(localNested, "rev-parse", "HEAD~1"));
        Assert.Equal("development", Git(localNested, "branch", "--show-current"));
        Assert.Equal(origin, Git(localNested, "remote", "get-url", "origin"));
        Assert.Equal("on host only", File.ReadAllText(Path.Combine(localNested, "tracked.txt")).Trim());
        Assert.Equal(string.Empty, Git(hostNested, "status", "--porcelain"));
    }

    [Fact]
    public async Task Alignment_refuses_dirty_or_diverged_repositories_and_leaves_the_work_in_place()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string host = pair.A.Work, local = pair.B.Work;
        MakeRepo(host);
        Git(pair.B.Root, "clone", "-q", host, local);
        Configure(local);
        string initial = Head(local);
        File.WriteAllText(Path.Combine(host, "tracked.txt"), "host\n");
        Git(host, "commit", "-qam", "Remote commit");

        string file = Path.Combine(local, "tracked.txt");
        File.WriteAllText(file, "local\n");
        var dirty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HexLinkProvision.Align(pair.PeerA, pair.B.Workspace.Folders[0], null, CancellationToken.None));
        Assert.Contains("uncommitted", dirty.Message);
        Assert.Equal(initial, Head(local));
        Assert.Equal("local\n", File.ReadAllText(file));

        Git(local, "commit", "-qam", "Different local commit");
        string diverged = Head(local);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HexLinkProvision.Align(pair.PeerA, pair.B.Workspace.Folders[0], null, CancellationToken.None));
        Assert.Contains("left behind", error.Message);
        Assert.Equal(diverged, Head(local));
        Assert.Equal("local\n", File.ReadAllText(file));
    }

    private static void MakeRepo(string path, string? ignores = null)
    {
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "development");
        Configure(path);
        File.WriteAllText(Path.Combine(path, "tracked.txt"), "first\n");
        if (ignores is not null)
            File.WriteAllText(Path.Combine(path, ".gitignore"), ignores);
        Git(path, "add", ".");
        Git(path, "commit", "-qm", "First");
    }

    private static void Configure(string path)
    {
        Git(path, "config", "user.email", "test@example.com");
        Git(path, "config", "user.name", "Test");
        Git(path, "config", "core.autocrlf", "false");
    }

    private static string Head(string folder) => Git(folder, "rev-parse", "HEAD");

    private static string Git(string folder, params string[] args)
    {
        GitResult git = GitProcess.Run(folder, args);
        Assert.True(git.Ok, $"git {string.Join(' ', args)} in {folder}: {git.Error}");
        return git.Output.Trim();
    }
}
