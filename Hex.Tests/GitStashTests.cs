using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>
/// The stash and what git is in the middle of, read out of git's own words first, then against real repositories made
/// for the test when there's a git to make them with.
/// </summary>
public sealed class GitStashTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-stash-" + Guid.NewGuid().ToString("N"));

    public GitStashTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        // git makes its objects read only, which Directory.Delete won't touch
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string At(string relative) => Path.GetFullPath(Path.Combine(root, relative));

    private void Git(params string[] arguments)
    {
        GitResult result = GitProcess.Run(root, arguments);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} said {result.Why}");
    }

    // A repository with one commit of a.txt in it, on main
    private GitRepository Made()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Tester");
        Git("config", "user.email", "tester@example.com");
        Git("config", "core.autocrlf", "false");
        File.WriteAllText(At("a.txt"), "one\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "First");
        return GitRepository.Find(root)!;
    }

    [Fact]
    public void Stash_list_reads_the_name_the_message_and_when()
    {
        string output = "stash@{0}\0On main: half done\01760000000\nstash@{1}\0WIP on main: abc1234 First\01750000000\n";

        IReadOnlyList<GitStashEntry> entries = GitStashEntry.Parse(output);

        Assert.Equal([0, 1], entries.Select(entry => entry.Index));
        Assert.Equal("stash@{1}", entries[1].Name);
        Assert.Equal("half done", entries[0].Short);
        Assert.Equal("WIP on main: abc1234 First", entries[1].Short);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), entries[0].When);
    }

    [Fact]
    public void Push_stashes_and_pop_brings_it_back()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        File.WriteAllText(At("a.txt"), "two\n");

        Assert.Null(repository.PushStash("half done", includeUntracked: false));
        Assert.Equal("one\n", File.ReadAllText(At("a.txt")));
        IReadOnlyList<GitStashEntry> stashes = repository.List();
        Assert.Single(stashes);
        Assert.Equal("half done", stashes[0].Short);

        Assert.Null(repository.Pop(0));
        Assert.Equal("two\n", File.ReadAllText(At("a.txt")));
        Assert.Empty(repository.List());
    }

    [Fact]
    public void Nothing_to_stash_is_said_and_untracked_only_go_when_asked()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        Assert.Contains("No local changes to save", repository.PushStash(null, includeUntracked: false));

        File.WriteAllText(At("new.txt"), "fresh\n");
        Assert.NotNull(repository.PushStash(null, includeUntracked: false));
        Assert.True(File.Exists(At("new.txt")));

        Assert.Null(repository.PushStash("with the new one", includeUntracked: true));
        Assert.False(File.Exists(At("new.txt")));
        Assert.Contains("fresh", repository.Show(0).Patch);

        Assert.Null(repository.Apply(0));
        Assert.True(File.Exists(At("new.txt")));
        Assert.Single(repository.List());
    }

    [Fact]
    public void Drop_throws_the_one_asked_for_away()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        File.WriteAllText(At("a.txt"), "first change\n");
        repository.PushStash("older", includeUntracked: false);
        File.WriteAllText(At("a.txt"), "second change\n");
        repository.PushStash("newer", includeUntracked: false);

        Assert.Null(repository.Drop(1));
        Assert.Equal(["newer"], repository.List().Select(entry => entry.Short));
        Assert.NotNull(repository.Drop(5));
    }

    [Fact]
    public void A_merge_with_conflicts_is_merging_until_aborted()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        Assert.Equal(GitOperation.None, repository.Operation());

        Git("switch", "-q", "-c", "other");
        File.WriteAllText(At("a.txt"), "theirs\n");
        Git("commit", "-q", "-am", "Theirs");
        Git("switch", "-q", "main");
        File.WriteAllText(At("a.txt"), "ours\n");
        Git("commit", "-q", "-am", "Ours");

        Assert.False(GitProcess.Run(root, ["merge", "other"]).Ok);
        Assert.Equal(GitOperation.Merging, repository.Operation());

        Assert.Null(repository.Abort(GitOperation.Merging));
        Assert.Equal(GitOperation.None, repository.Operation());
        Assert.Equal("ours\n", File.ReadAllText(At("a.txt")));
    }

    [Fact]
    public void A_rebase_with_conflicts_is_rebasing_and_carries_on_once_settled()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        Git("switch", "-q", "-c", "other");
        File.WriteAllText(At("a.txt"), "theirs\n");
        Git("commit", "-q", "-am", "Theirs");
        Git("switch", "-q", "main");
        File.WriteAllText(At("a.txt"), "ours\n");
        Git("commit", "-q", "-am", "Ours");

        Assert.False(GitProcess.Run(root, ["rebase", "other"]).Ok);
        Assert.Equal(GitOperation.Rebasing, repository.Operation());

        File.WriteAllText(At("a.txt"), "both\n");
        Git("add", "a.txt");
        Assert.Null(repository.Continue(GitOperation.Rebasing));
        Assert.Equal(GitOperation.None, repository.Operation());
        Assert.Equal("Ours", GitProcess.Run(root, ["log", "-1", "--format=%s"]).Output.Trim());
    }

    [Fact]
    public void A_worktree_with_a_git_file_is_read_through_it()
    {
        if (!GitProcess.Installed)
            return;

        Made();
        string tree = At("tree");
        Git("worktree", "add", "-q", "-b", "elsewhere", tree);
        GitRepository repository = GitRepository.Find(tree)!;
        Assert.True(File.Exists(Path.Combine(tree, ".git")));

        File.WriteAllText(Path.Combine(tree, "a.txt"), "theirs\n");
        Assert.True(GitProcess.Run(tree, ["commit", "-q", "-am", "Theirs"]).Ok);
        File.WriteAllText(At("a.txt"), "ours\n");
        Git("commit", "-q", "-am", "Ours");

        Assert.False(GitProcess.Run(tree, ["merge", "main"]).Ok);
        Assert.Equal(GitOperation.Merging, repository.Operation());
        Assert.Equal(GitOperation.None, GitRepository.Find(root)!.Operation());
        Assert.Null(repository.Abort(GitOperation.Merging));
    }
}
