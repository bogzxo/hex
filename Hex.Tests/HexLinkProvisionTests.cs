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
        string uninitialized = "missing-child";
        string uninitializedHead = Head(moduleSource);
        File.WriteAllText(Path.Combine(moduleSource, ".gitmodules"),
            $"[submodule \"{uninitialized}\"]\n\tpath = {uninitialized}\n\turl = https://invalid.example/uninitialized.git\n");
        Git(moduleSource, "update-index", "--add", "--cacheinfo", $"160000,{uninitializedHead},{uninitialized}");
        Git(moduleSource, "add", ".gitmodules");
        Git(moduleSource, "commit", "-qm", "Record a submodule to leave uninitialized");
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

        HexLinkProvisionResult got = await HexLinkProvision.Get(pair.PeerA, destination, journal, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(3));
        var local = Assert.Single(got.Folders);
        var skipped = Assert.Single(got.Skipped);
        Assert.Equal("nested/modules/sub/" + uninitialized, skipped.Path);
        Assert.Contains("uninitialized", skipped.Reason);
        Assert.Equal("work", local.Name);
        Assert.Equal(Path.GetFullPath(destination), local.Path);
        string localNested = Path.Combine(destination, "nested"), localModule = Path.Combine(localNested, "modules", "sub");
        Assert.False(HexLinkProvision.HasRepository(Path.Combine(localModule, uninitialized)));
        Assert.Contains(uninitializedHead, Git(localModule, "ls-tree", "HEAD", uninitialized));
        Assert.Equal("https://invalid.example/uninitialized.git",
            Git(localModule, "config", "-f", ".gitmodules", "--get", "submodule." + uninitialized + ".url"));
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

    [Fact]
    public async Task One_sided_repositories_wait_while_common_files_sync_and_get_only_installs_the_requested_repo()
    {
        if (HexLinkGit.Why() is not null)
            return;

        var edit = HexLinkRights.View | HexLinkRights.Edit;
        using var pair = await LinkedPair.Up(edit, edit);
        string host = pair.A.Work, guest = pair.B.Work;
        string chosen = Path.Combine(host, "chosen"), target = Path.Combine(guest, "chosen");
        MakeRepo(host, "/chosen/\n/other/\n/guest-only/\n");
        Git(pair.B.Root, "clone", "-q", host, guest);
        MakeRepo(chosen, "/independent/\n");
        MakeRepo(Path.Combine(chosen, "independent"));
        MakeRepo(Path.Combine(host, "other"));
        MakeRepo(Path.Combine(guest, "guest-only"));
        string source = Path.Combine(pair.A.Root, "module-source");
        MakeRepo(source);
        Git(chosen, "remote", "add", "origin", "https://invalid.example/chosen.git");
        Git(chosen, "-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "module");
        Git(chosen, "commit", "-qam", "Add module");
        File.Delete(Path.Combine(chosen, "tracked.txt"));
        File.WriteAllText(Path.Combine(chosen, "new.txt"), "only in chosen\n");
        File.WriteAllText(Path.Combine(chosen, "module", "new.txt"), "only in module\n");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken stop = timeout.Token;
        using var sync = new HexLinkSync(pair.PeerB, pair.A.Workspace.Folders[0], Path.Combine(pair.A.Root, "sync"));
        await sync.Start(cancel: stop);
        await sync.Flush(stop);
        Assert.Equal(HexLinkSyncState.Idle, sync.State);
        Assert.Equal(["guest-only"], sync.MissingRepositories.Here);
        Assert.Equal(["chosen", "chosen/independent", "chosen/module", "other"], sync.MissingRepositories.There);

        File.WriteAllText(Path.Combine(host, "top.txt"), "sync this\n");
        File.WriteAllText(Path.Combine(chosen, "watch.txt"), "withhold this\n");
        await sync.Flush(stop);
        Assert.Equal("sync this", File.ReadAllText(Path.Combine(guest, "top.txt")).Trim());
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(host, "guest-only")));
        Assert.True(File.Exists(Path.Combine(guest, "guest-only", "tracked.txt")));

        Directory.CreateDirectory(target);
        string placeholder = Path.Combine(target, "keep.txt");
        File.WriteAllText(placeholder, "guest work\n");
        string journal = Path.Combine(pair.B.Root, "state", "chosen.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HexLinkProvision.GetMissingRepository(pair.PeerA, pair.B.Workspace.Folders[0], "chosen", journal, null, stop));
        Assert.Equal("guest work", File.ReadAllText(placeholder).Trim());
        File.Delete(placeholder);

        await HexLinkProvision.GetMissingRepository(pair.PeerA, pair.B.Workspace.Folders[0], "chosen", journal, null, stop);
        Assert.Equal(Head(chosen), Head(target));
        Assert.Equal("https://invalid.example/chosen.git", Git(target, "remote", "get-url", "origin"));
        Assert.Equal(Head(Path.Combine(chosen, "module")), Head(Path.Combine(target, "module")));
        Assert.Equal("only in module", File.ReadAllText(Path.Combine(target, "module", "new.txt")).Trim());
        Assert.Equal("only in chosen", File.ReadAllText(Path.Combine(target, "new.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(target, "tracked.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "independent")));
        Assert.False(Directory.Exists(Path.Combine(guest, "other")));

        await sync.StopForRepositoryRefresh();
        await sync.Start(cancel: stop);
        File.WriteAllText(Path.Combine(target, "watch.txt"), "sync this after the get\n");
        await sync.Flush(stop);
        Assert.Equal("sync this after the get", File.ReadAllText(Path.Combine(chosen, "watch.txt")).Trim());
        Assert.Equal(["chosen/independent", "other"], sync.MissingRepositories.There);

        await sync.StopForRepositoryRefresh();
        Directory.Move(target, Path.Combine(pair.B.Root, "parked-checkout"));
        await sync.Start(cancel: stop);
        await sync.Flush(stop);
        Assert.True(File.Exists(Path.Combine(chosen, "watch.txt")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A_missing_submodule_does_not_hide_a_head_mismatch_in_its_parent()
    {
        string first = new('a', 40), second = new('b', 40);
        var here = new HexLinkManifest("work", first, [], first,
            NestedHeads: new Dictionary<string, string?> { ["nested"] = first, ["nested/sub"] = first });
        var there = new HexLinkManifest("work", first, [], first,
            NestedHeads: new Dictionary<string, string?> { ["nested"] = second });
        var missing = HexLinkMissingRepositories.Compare(here, there);

        Assert.True(missing.Blocks("nested/sub/file"));
        Assert.True(missing.Blocks("nested"));
        Assert.False(missing.Blocks("nested/main.txt"));
        Assert.StartsWith("nested is at ", HexLinkTree.CommitsDiffer(here, there, missing));
    }

    [Fact]
    public async Task Get_skips_a_nested_bundle_failure_but_a_failed_root_is_fatal()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string host = pair.A.Work;
        MakeRepo(host, "/ok/\n/skipped/\n");
        MakeRepo(Path.Combine(host, "ok"));
        MakeRepo(Path.Combine(host, "skipped"));
        File.WriteAllText(Path.Combine(host, "skipped", "private.txt"), "This subtree must stay put.");
        File.WriteAllText(Path.Combine(host, "ok", "new.txt"), "Get the other repository.");

        string journal = Path.Combine(pair.B.Root, "partial.json");
        string refused = Path.Combine(pair.B.Root, "root-failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<IOException>(() => HexLinkProvision.Get(pair.PeerA, refused, journal,
            null, timeout.Token, new RefuseBundle(pair.PeerA, "-0")));
        Assert.False(Directory.Exists(refused));

        string destination = Path.Combine(pair.B.Root, "nested-failed");
        HexLinkProvisionResult got = await HexLinkProvision.Get(pair.PeerA, destination, journal,
            null, timeout.Token, new RefuseBundle(pair.PeerA, "-2"));
        Assert.Equal(new HexLinkProvisionSkip("skipped", "Git could not make this bundle."), Assert.Single(got.Skipped));
        Assert.Equal(Head(host), Head(destination));
        Assert.Equal(Head(Path.Combine(host, "ok")), Head(Path.Combine(destination, "ok")));
        Assert.True(File.Exists(Path.Combine(destination, "ok", "new.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "skipped")));
        Assert.True(File.Exists(Path.Combine(host, "skipped", "private.txt")));
    }

    private sealed class RefuseBundle(IHexLinkBundleSource next, string suffix) : IHexLinkBundleSource
    {
        public Task<string?> TransferBundle(string repository, IReadOnlyList<string> have, Action<long, long> progress,
            string? verifyIn, CancellationToken cancel) =>
            repository.EndsWith(suffix, StringComparison.Ordinal)
                ? Task.FromException<string?>(new IOException("Git could not make this bundle."))
                : next.TransferBundle(repository, have, progress, verifyIn, cancel);
    }

    private static string Head(string folder) => Git(folder, "rev-parse", "HEAD");

    private static string Git(string folder, params string[] args)
    {
        GitResult git = GitProcess.Run(folder, args, patience: TimeSpan.FromMinutes(2));
        Assert.True(git.Ok, $"git {string.Join(' ', args)} in {folder}: {git.Error}");
        return git.Output.Trim();
    }
}
