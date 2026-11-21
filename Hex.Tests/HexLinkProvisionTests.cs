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

    [Fact]
    public async Task Empty_destination_gets_history_submodules_and_the_working_tree_and_can_resume_after_cancellation()
    {
        if (HexLinkGit.Why() is not null)
            return;

        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        string host = pair.A.Work, nested = Path.Combine(host, "nested");
        string moduleSource = Path.Combine(pair.A.Root, "sub-source");
        MakeRepo(host, "/nested/\nout/\n");
        MakeRepo(nested);
        MakeRepo(moduleSource);
        Git(host, "remote", "add", "origin", "https://invalid.example/workspace.git");
        Git(nested, "remote", "add", "origin", "https://invalid.example/nested.git");
        Git(nested, "-c", "protocol.file.allow=always", "submodule", "add", "-q", moduleSource, "modules/sub");
        Git(nested, "commit", "-qam", "Add submodule locally");
        string module = Path.Combine(nested, "modules", "sub");
        Git(module, "remote", "set-url", "origin", "https://invalid.example/sub.git");
        File.WriteAllText(Path.Combine(host, "tracked.txt"), "dirty root\n");
        File.Delete(Path.Combine(nested, "tracked.txt"));
        File.WriteAllText(Path.Combine(module, "new.txt"), "new module file\n");
        Directory.CreateDirectory(Path.Combine(host, "out"));
        File.WriteAllText(Path.Combine(host, "out", "obj.dll"), "ignore");

        string destination = Path.Combine(pair.B.Root, "empty-destination");
        string journal = Path.Combine(pair.B.Root, "state", "upload.json");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HexLinkProvision.Get(pair.PeerA, destination, journal,
            new InlineProgress(progress => { if (progress.FilesDone > 0) cancellation.Cancel(); }), cancellation.Token)
            .WaitAsync(TimeSpan.FromMinutes(3)));
        Assert.True(File.Exists(journal));

        IReadOnlyList<HexLinkFolder> folders = await HexLinkProvision.Get(pair.PeerA, destination, journal, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(3));
        var local = Assert.Single(folders);
        Assert.Equal("work", local.Name);
        Assert.Equal(Path.GetFullPath(destination), local.Path);
        string localNested = Path.Combine(destination, "nested"), localModule = Path.Combine(localNested, "modules", "sub");
        Assert.Equal(Head(host), Head(destination));
        Assert.Equal(Head(nested), Head(localNested));
        Assert.Equal(Head(module), Head(localModule));
        Assert.Equal("2", Git(localNested, "rev-list", "--count", "HEAD"));
        Assert.Equal("development", Git(localNested, "branch", "--show-current"));
        Assert.Equal(string.Empty, Git(localModule, "branch", "--show-current"));
        Assert.Equal("https://invalid.example/workspace.git", Git(destination, "remote", "get-url", "origin"));
        Assert.Equal("https://invalid.example/nested.git", Git(localNested, "remote", "get-url", "origin"));
        Assert.Equal("https://invalid.example/sub.git", Git(localModule, "remote", "get-url", "origin"));
        Assert.True(File.Exists(Path.Combine(localModule, ".git")));
        Assert.Equal("dirty root\n", File.ReadAllText(Path.Combine(destination, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(localNested, "tracked.txt")));
        Assert.Equal("new module file\n", File.ReadAllText(Path.Combine(localModule, "new.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "out")));

        using var sync = new HexLinkSync(pair.PeerA, local, Path.Combine(pair.B.Root, "sync-state"));
        await sync.Start();
        await sync.Flush().WaitAsync(TimeSpan.FromSeconds(30));
        File.WriteAllText(Path.Combine(module, "new.txt"), "host later\n");
        await sync.Flush().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(Eventually(() => File.ReadAllText(Path.Combine(localModule, "new.txt")).ReplaceLineEndings("\n") == "host later\n"));
        File.WriteAllText(Path.Combine(localNested, "joiner.txt"), "joiner later\n");
        await sync.Flush().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(Path.Combine(nested, "joiner.txt")));
    }

    private sealed class InlineProgress(Action<HexLinkProvisionProgress> report) : IProgress<HexLinkProvisionProgress>
    {
        public void Report(HexLinkProvisionProgress progress) => report(progress);
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
