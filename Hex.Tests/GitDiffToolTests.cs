using Hex.Core.Git;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>hex_git_diff's diff, staged and unstaged edits to one file each as git says them, and its bound.</summary>
public sealed class GitDiffToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-gitdiff-" + Guid.NewGuid().ToString("N"));

    public GitDiffToolTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
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

    private string Git(params string[] arguments)
    {
        GitResult result = GitProcess.Run(root, arguments);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} said {result.Why}");
        return result.Output;
    }

    [Fact]
    public void Staged_and_unstaged_edits_to_the_same_file_are_each_what_git_says()
    {
        if (!GitProcess.Installed)
            return;

        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Tester");
        Git("config", "user.email", "tester@example.com");
        Git("config", "core.autocrlf", "false");
        Directory.CreateDirectory(At("src"));
        File.WriteAllText(At("src/a.txt"), "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n");
        File.WriteAllText(At("other.txt"), "x\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "First");

        File.WriteAllText(At("src/a.txt"), "ONE\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n");
        Git("add", "src/a.txt");
        File.WriteAllText(At("src/a.txt"), "ONE\ntwo\nthree\nfour\nfive\nsix\nseven\nEIGHT\n");
        File.WriteAllText(At("other.txt"), "y\n");

        GitRepository repository = GitRepository.Find(root)!;
        string staged = repository.Diff(At("src/a.txt"), staged: true)!;
        string unstaged = repository.Diff(At("src/a.txt"), staged: false)!;
        Assert.Equal(Git("diff", "--no-color", "--cached", "--", "src/a.txt"), staged);
        Assert.Equal(Git("diff", "--no-color", "--", "src/a.txt"), unstaged);
        Assert.Contains("+ONE", staged);
        Assert.DoesNotContain("EIGHT", staged);
        Assert.Contains("+EIGHT", unstaged);
        Assert.DoesNotContain("+ONE", unstaged);

        // Scoped to the folder, the other file's change isn't in it
        Assert.DoesNotContain("other.txt", repository.Diff(At("src"), staged: false));
        Assert.Contains("other.txt", repository.Diff(null, staged: false));
    }

    [Fact]
    public void A_long_diff_is_cut_at_a_line_and_says_so()
    {
        string text = string.Concat(Enumerable.Range(0, 500).Select(n => $"+line {n}\n"));
        string bounded = DawnScene.Bounded(text, 1000);
        Assert.True(bounded.Length <= 1000);
        Assert.EndsWith($"characters shown, narrow path, pick one view or raise max_chars]", bounded);
        Assert.Contains($"of {text.Length} characters", bounded);
        Assert.Equal(text, DawnScene.Bounded(text, text.Length));
    }
}
