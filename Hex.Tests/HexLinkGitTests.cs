using System.Text;

using Hex.Core;
using Hex.Core.Git;
using Hex.Dawn;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

internal sealed class LinkGitFactAttribute : FactAttribute
{
    public LinkGitFactAttribute(bool directoryAlias = false) =>
        Skip = directoryAlias && OperatingSystem.IsWindows()
            ? "Creating a directory symlink may require Developer Mode on Windows."
            : HexLinkGit.Why();
}

/// <summary>
/// A repository handed over a link by git: bundled, sent in chunks, cloned on the other side, then a second fetch
/// bringing only what's new; a patch of the working tree offered and applied. And the working-tree sync's manifest and
/// watch. Each passes over quietly where git isn't installed.
/// </summary>
public sealed class HexLinkGitTests
{
    private static bool NoGit => HexLinkGit.Why() is not null;

    private static void Git(string folder, params string[] arguments)
    {
        GitResult done = GitProcess.Run(folder, arguments);
        Assert.True(done.Ok, $"git {string.Join(' ', arguments)}: {done.Error}");
    }

    private static string Head(string folder, string what = "HEAD") => GitProcess.Run(folder, ["rev-parse", what]).Output.Trim();

    // A repository with a commit, in a folder of the workspace
    private static string MadeRepository(LinkServices services)
    {
        string repo = services.Work;
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test");
        Git(repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repo, "readme.md"), "one\n");
        File.WriteAllBytes(Path.Combine(repo, "logo.bin"), [0, 1, 2, 3, 0, 255]);
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "first");
        services.Workspace = services.Workspace with { Repositories = [new HexLinkRepository("work", "main", Head(repo), repo)] };
        return repo;
    }

    [LinkGitFact]
    public async Task A_bundle_clones_on_the_other_side_and_a_second_fetch_brings_only_whats_new()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string repo = MadeRepository(pair.A);

        long last = 0;
        string bundle = (await pair.PeerA.Bundle("work", [], (got, _) => last = got))!;
        Assert.True(File.Exists(bundle));
        Assert.Equal(new FileInfo(bundle).Length, last);
        string clone = Path.Combine(pair.B.Root, "clone");
        Assert.Null(HexLinkGit.Clone(bundle, clone));
        File.Delete(bundle);
        // As this machine's git checks it out, line ends and all
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(clone, "readme.md")).ReplaceLineEndings("\n"));
        Assert.Equal(Head(repo), Head(clone));
        Assert.Equal(string.Empty, GitProcess.Run(clone, ["remote"]).Output.Trim());

        // Nothing new: nothing comes
        Assert.Null(await pair.PeerA.Bundle("work", HexLinkGit.Haves(clone)));

        File.WriteAllText(Path.Combine(repo, "readme.md"), "one\ntwo\n");
        Git(repo, "commit", "-q", "-am", "second");
        string again = (await pair.PeerA.Bundle("work", HexLinkGit.Haves(clone), repo: clone))!;
        GitResult verified = GitProcess.Run(clone, ["bundle", "verify", again]);
        Assert.Contains("requires", verified.Output + verified.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(HexLinkGit.Fetch(again, clone, "Desk PC"));
        File.Delete(again);
        Assert.Equal(Head(repo), Head(clone, "refs/remotes/desk-pc/main"));
        Assert.Equal(GitProcess.Run(repo, ["rev-parse", "HEAD~1"]).Output.Trim(), Head(clone));

        string receiveDirectory = Directory.CreateDirectory(Path.Combine(pair.B.Root, "missing-bundle")).FullName;
        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.Bundle("nothing", [], temporaryDirectory: receiveDirectory)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(receiveDirectory));
    }

    [LinkGitFact]
    public async Task A_failed_git_tip_read_reports_the_repository_reason()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var (tips, issues) = await pair.PeerA.GitFollowTips("work", [""], CancellationToken.None);

        Assert.Empty(tips);
        Assert.Equal("The selected folder has no Git checkout of its own.", issues[""]);
    }

    [Fact]
    public void Git_ownership_refusals_tell_the_user_which_account_to_check()
    {
        var error = new GitResult(128, "", "fatal: detected dubious ownership in repository at '/some/work'");
        Assert.Contains("different owner", HexLinkGit.ErrorOf(error));
        Assert.Contains("safe.directory", HexLinkGit.ErrorOf(error));
        Assert.Contains("account that runs Hex", HexLinkGit.ErrorOf(error));
    }

    [LinkGitFact(directoryAlias: true)]
    public async Task Git_follow_accepts_an_aliased_checkout_root_but_not_a_subdirectory()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string repo = MadeRepository(pair.A);
        string alias = Path.Combine(pair.B.Root, "alias");
        Directory.CreateSymbolicLink(alias, repo);

        Assert.Equal(Head(repo), HexLinkGitFollow.ReadCheckout(alias, CancellationToken.None).Head);
        string subdirectory = Directory.CreateDirectory(Path.Combine(repo, "sub")).FullName;
        Assert.Throws<HexLinkException>(() => HexLinkGitFollow.ReadCheckout(subdirectory, CancellationToken.None));
    }

    [Fact]
    public async Task A_patch_of_the_working_tree_is_offered_and_applies_on_a_clone_binary_files_left_out()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string repo = MadeRepository(pair.A);
        string clone = Path.Combine(pair.B.Root, "clone");
        string bundle = (await pair.PeerA.Bundle("work", []))!;
        Assert.Null(HexLinkGit.Clone(bundle, clone));
        File.Delete(bundle);
        // The clone's checkout as the repository's, whatever this machine's git does with line ends
        Git(clone, "config", "core.autocrlf", "false");
        File.Delete(Path.Combine(clone, "readme.md"));
        Git(clone, "checkout", "-q", "--", ".");

        File.WriteAllText(Path.Combine(repo, "readme.md"), "one\nchanged\n");
        File.WriteAllBytes(Path.Combine(repo, "logo.bin"), [9, 0, 9]);
        string diff = HexLinkGit.Diff(repo);
        Assert.Contains("+changed", diff);
        Assert.DoesNotContain("logo.bin", diff);

        // Offered from B's side to A's person: here A offers to B, whose answer is applied on its clone
        pair.B.PatchAnswer = "applied";
        Assert.Equal("applied", await pair.PeerB.OfferPatch("Readme", diff));
        var (title, offered) = Assert.Single(pair.B.Offered);
        Assert.Equal("Readme", title);
        Assert.Null(HexLinkGit.Apply(clone, offered));
        Assert.Equal("one\nchanged\n", File.ReadAllText(Path.Combine(clone, "readme.md")));
        Assert.NotNull(HexLinkGit.Apply(clone, offered));
    }

    [Fact]
    public void A_device_name_is_a_refs_folder()
    {
        Assert.Equal("desk-pc", HexLinkGit.Slug("Desk PC"));
        Assert.Equal("bogdan-s-macbook", HexLinkGit.Slug("Bogdan's MacBook!"));
        Assert.Equal("device", HexLinkGit.Slug("../.."));
        Assert.True(HexLinkGit.IsCommitId(new string('a', 40)));
        Assert.False(HexLinkGit.IsCommitId("HEAD"));
        Assert.False(HexLinkGit.IsCommitId(new string('g', 40)));
    }

    [Fact]
    public async Task The_manifest_has_what_git_carries_and_not_what_it_ignores()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string repo = MadeRepository(pair.A);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        File.WriteAllText(Path.Combine(repo, "bin", "out.dll"), "built");
        File.WriteAllText(Path.Combine(repo, "untracked.cs"), "class U {}");
        using (var big = File.Create(Path.Combine(repo, "big.dat")))
            big.SetLength(HexLinkFiles.MOST_READ + 10);

        HexLinkManifest manifest = await pair.PeerA.Manifest("work");
        Assert.Equal("work", manifest.Folder);
        Assert.Equal(Head(repo), manifest.Head);
        string[] paths = [.. manifest.Files.Select(file => file.Path).Order(StringComparer.Ordinal)];
        Assert.Equal(["work/.gitignore", "work/big.dat", "work/logo.bin", "work/readme.md", "work/untracked.cs"], paths);
        Assert.Equal(HexLinkFiles.Hash(File.ReadAllBytes(Path.Combine(repo, "readme.md"))), manifest.Files.Single(file => file.Path == "work/readme.md").Hash);
        Assert.True(manifest.Files.Single(file => file.Path == "work/big.dat").Big);

        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.Manifest("nowhere")));
    }

    [Fact]
    public void Exactly_one_side_owns_automatic_sync_when_either_side_can_view()
    {
        HexLinkRights view = HexLinkRights.View, edit = view | HexLinkRights.Edit;
        HexLinkRights[] grants = [HexLinkRights.None, view, edit];
        foreach (HexLinkRights listenerGrants in grants)
        foreach (HexLinkRights diallerGrants in grants)
        {
            bool listenerOwns = DawnScene.LinkAutoSyncOwner(false, listenerGrants, diallerGrants);
            bool diallerOwns = DawnScene.LinkAutoSyncOwner(true, diallerGrants, listenerGrants);
            Assert.Equal(listenerGrants == HexLinkRights.None && diallerGrants == HexLinkRights.None ? 0 : 1,
                (listenerOwns ? 1 : 0) + (diallerOwns ? 1 : 0));
            if (listenerGrants == edit && diallerGrants != edit)
                Assert.True(diallerOwns);
            if (diallerGrants == edit && listenerGrants != edit)
                Assert.True(listenerOwns);
        }
        Assert.True(DawnScene.LinkAutoSyncOwner(false, edit, edit));
    }

    [Fact]
    public async Task A_shared_root_carries_ignored_nested_checkouts_and_their_submodules()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string root = MadeRepository(pair.A);
        string nested = Path.Combine(root, "nested");
        string source = Path.Combine(pair.B.Root, "sub-source");
        foreach (string repository in new[] { nested, source })
        {
            Directory.CreateDirectory(repository);
            Git(repository, "init", "-q", "-b", "development");
            Git(repository, "config", "user.email", "test@example.com");
            Git(repository, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(repository, ".gitignore"), "out/\n");
            File.WriteAllText(Path.Combine(repository, "tracked.txt"), "commit\n");
            Git(repository, "add", ".");
            Git(repository, "commit", "-qm", "first");
        }

        File.WriteAllText(Path.Combine(root, ".gitignore"), "/nested/\n");
        Git(nested, "-c", "protocol.file.allow=always", "submodule", "add", "-q", source, "lib/module");
        Git(nested, "commit", "-qam", "Add submodule");
        string submodule = Path.Combine(nested, "lib", "module");
        File.WriteAllText(Path.Combine(submodule, "new.txt"), "untracked\n");
        Directory.CreateDirectory(Path.Combine(submodule, "out"));
        File.WriteAllText(Path.Combine(submodule, "out", "build.txt"), "ignored\n");

        var changes = new System.Collections.Concurrent.ConcurrentQueue<HexLinkTreeChange>();
        HexLinkManifest manifest = await pair.PeerA.WatchTree("work", batch =>
        {
            foreach (HexLinkTreeChange change in batch)
                changes.Enqueue(change);
        });

        Assert.Equal(Head(nested), manifest.NestedHeads!["nested"]);
        Assert.Equal(Head(submodule), manifest.NestedHeads["nested/lib/module"]);
        Assert.Contains(manifest.Files, file => file.Path == "work/nested/tracked.txt");
        Assert.Contains(manifest.Files, file => file.Path == "work/nested/lib/module/new.txt");
        Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("/out/") || file.Path.EndsWith("/.git"));
        Assert.Null(HexLinkTree.CommitsDiffer(manifest, HexLinkTree.Of(HexLinkTree.Json(manifest))));

        var tree = new HexLinkTree(new HexLinkFolder("work", root));
        tree.Manifest();
        Assert.Equal(["nested/lib/module/out/build.txt"], tree.Ignored(["nested/tracked.txt", "nested/lib/module/new.txt", "nested/lib/module/out/build.txt"]));

        File.WriteAllText(Path.Combine(submodule, "later.txt"), "live\n");
        Assert.True(Eventually(() => changes.Any(change => change.Path == "work/nested/lib/module/later.txt")));
        Assert.DoesNotContain(changes, change => change.Path.Contains("/out/"));

        Git(nested, "commit", "--allow-empty", "-qm", "Nested head changed");
        HexLinkManifest moved = await pair.PeerA.Manifest("work");
        Assert.Equal(manifest.Head, moved.Head);
        Assert.StartsWith("nested is at ", HexLinkTree.CommitsDiffer(manifest, moved));
    }

    [Fact]
    public async Task A_folder_that_isnt_a_repository_is_walked_without_what_builds_make()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string work = pair.A.Work;
        foreach (string skipped in new[] { "bin", "obj", "node_modules", ".git" })
        {
            Directory.CreateDirectory(Path.Combine(work, skipped));
            File.WriteAllText(Path.Combine(work, skipped, "x.txt"), "x");
        }

        Directory.CreateDirectory(Path.Combine(work, "src"));
        File.WriteAllText(Path.Combine(work, "src", "a.cs"), "a");
        HexLinkManifest manifest = await pair.PeerA.Manifest("work");
        // The temp folder may sit in a repository of its own on some machines; only a walk leaves bin out by name alone
        Assert.Contains("work/src/a.cs", manifest.Files.Select(file => file.Path));
        Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("/.git/", StringComparison.Ordinal) || file.Path.Contains("node_modules", StringComparison.Ordinal));

        var tree = new HexLinkTree(new HexLinkFolder("work", work));
        HexLinkManifest first = tree.Manifest(), second = tree.Manifest();
        Assert.Equal(first.Files.Select(file => file.Hash), second.Files.Select(file => file.Hash));
    }

    [Fact]
    public async Task A_watched_tree_says_what_really_changed_and_flush_answers_after_its_said()
    {
        if (NoGit)
            return;
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        string repo = MadeRepository(pair.A);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        var heard = new System.Collections.Concurrent.ConcurrentQueue<HexLinkTreeChange>();
        HexLinkManifest manifest = await pair.PeerA.WatchTree("work", changes =>
        {
            foreach (HexLinkTreeChange change in changes)
                heard.Enqueue(change);
        });
        Assert.Contains(manifest.Files, file => file.Path == "work/readme.md");

        File.WriteAllText(Path.Combine(repo, "readme.md"), "one\nmore\n");
        File.WriteAllText(Path.Combine(repo, "new.cs"), "class N {}");
        File.WriteAllText(Path.Combine(repo, "bin", "ignored.dll"), "built");
        Assert.True(Eventually(() => heard.Any(change => change.Path == "work/readme.md") && heard.Any(change => change.Path == "work/new.cs")));
        Assert.Equal(HexLinkFiles.Hash(Encoding.UTF8.GetBytes("one\nmore\n")), heard.Last(change => change.Path == "work/readme.md").Hash);

        // The same bytes written again, a touch: nothing said
        await pair.PeerA.Flush("work");
        int count = heard.Count;
        File.WriteAllText(Path.Combine(repo, "readme.md"), "one\nmore\n");
        File.SetLastWriteTimeUtc(Path.Combine(repo, "readme.md"), DateTime.UtcNow.AddMinutes(1));
        await pair.PeerA.Flush("work");
        Assert.Equal(count, heard.Count);

        // Written and flushed at once: said before flush answers, the watcher not waited for
        File.WriteAllText(Path.Combine(repo, "flushed.cs"), "class F {}");
        File.Delete(Path.Combine(repo, "new.cs"));
        await pair.PeerA.Flush("work");
        Assert.Contains(heard, change => change.Path == "work/flushed.cs" && change.Hash is not null);
        Assert.Contains(heard, change => change.Path == "work/new.cs" && change.Deleted);
        Assert.DoesNotContain(heard, change => change.Path.Contains("bin/", StringComparison.Ordinal));

        await pair.PeerA.UnwatchTree("work");
        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.Flush("work")));
    }
}
