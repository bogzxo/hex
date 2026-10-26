using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>
/// The last commit read, amended and undone, and the message a merge leaves, against a real repository made for each
/// test when there's a git to make one with.
/// </summary>
public sealed class GitCommitsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-commits-" + Guid.NewGuid().ToString("N"));

    public GitCommitsTests() => Directory.CreateDirectory(root);

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

    private GitResult Git(params string[] arguments) => GitProcess.Run(root, arguments);

    private void Must(params string[] arguments)
    {
        GitResult result = Git(arguments);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} said {result.Why}");
    }

    // A repository with one commit of a.txt in it
    private GitRepository Made()
    {
        Must("init", "-q", "-b", "main");
        Must("config", "user.name", "Tester");
        Must("config", "user.email", "tester@example.com");
        File.WriteAllText(At("a.txt"), "one\n");
        Must("add", ".");
        Must("commit", "-q", "-m", "First go");
        return GitRepository.Find(root)!;
    }

    private string Subject() => Git("log", "-1", "--format=%s").Output.Trim();

    [Fact]
    public void Last_message_is_the_whole_of_it_and_null_before_any_commit()
    {
        if (!GitProcess.Installed)
            return;

        Must("init", "-q", "-b", "main");
        Assert.Null(GitRepository.Find(root)!.LastMessage());

        GitRepository repository = Made();
        File.WriteAllText(At("a.txt"), "two\n");
        Must("add", "a.txt");
        Assert.Null(repository.Commit("Second go\n\n# Why\nBecause"));
        Assert.Equal("Second go\n\n# Why\nBecause", repository.LastMessage());
    }

    [Fact]
    public void Amend_with_nothing_staged_changes_only_the_message()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        File.WriteAllText(At("a.txt"), "changed but not staged\n");

        Assert.Null(repository.Amend("First go, better said"));

        Assert.Equal("First go, better said", Subject());
        Assert.Equal("1", Git("rev-list", "--count", "HEAD").Output.Trim());
        Assert.Equal("one\n", Git("show", "HEAD:a.txt").Output);
    }

    [Fact]
    public void Amend_takes_what_is_staged_into_the_last_commit()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        File.WriteAllText(At("b.txt"), "new\n");
        Must("add", "b.txt");

        Assert.Null(repository.Amend("First go, with b"));

        Assert.Equal("1", Git("rev-list", "--count", "HEAD").Output.Trim());
        Assert.Equal("new\n", Git("show", "HEAD:b.txt").Output);
    }

    [Fact]
    public void Undo_leaves_the_changes_staged_and_refuses_the_first_commit()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        Assert.NotNull(repository.UndoLastCommit());
        Assert.Equal("First go", Subject());

        File.WriteAllText(At("a.txt"), "two\n");
        Must("commit", "-q", "-am", "Second go");

        Assert.Null(repository.UndoLastCommit());

        Assert.Equal("First go", Subject());
        Assert.Equal("a.txt", Git("diff", "--cached", "--name-only").Output.Trim());
        Assert.Equal("two\n", File.ReadAllText(At("a.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Merge_message_is_git_s_without_its_comments_and_null_without_a_merge()
    {
        if (!GitProcess.Installed)
            return;

        GitRepository repository = Made();
        Assert.Null(repository.MergeMessage());

        Must("switch", "-q", "-c", "other");
        File.WriteAllText(At("a.txt"), "theirs\n");
        Must("commit", "-q", "-am", "Theirs");
        Must("switch", "-q", "main");
        File.WriteAllText(At("a.txt"), "ours\n");
        Must("commit", "-q", "-am", "Ours");
        Assert.False(Git("merge", "other").Ok);

        string? message = repository.MergeMessage();
        Assert.NotNull(message);
        Assert.StartsWith("Merge branch 'other'", message);
        Assert.DoesNotContain("#", message);

        Must("merge", "--abort");
        Assert.Null(repository.MergeMessage());
    }
}
