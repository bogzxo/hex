using Hex.Core;
using Hex.Core.Git;

namespace Hex.Tests;

public sealed class HexLinkRepositoryAnswerTests
{
    [Fact]
    public void A_remembered_repository_answer_follows_commits_and_branch_changes()
    {
        if (HexLinkGit.Why() is not null)
            return;

        string repo = Path.Combine(Path.GetTempPath(), "hexlink-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run("init", "-q");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "one");
            Run("add", "a.txt");
            Commit("one");
            HexLinkRepository first = HexLinkWorkspaces.RepositoryOf(repo)!;
            Assert.Same(first, HexLinkWorkspaces.RepositoryOf(repo));

            File.WriteAllText(Path.Combine(repo, "a.txt"), "two");
            Run("add", "a.txt");
            Commit("two");
            HexLinkRepository second = HexLinkWorkspaces.RepositoryOf(repo)!;
            Assert.NotEqual(first.Head, second.Head);
            Assert.Equal(first.Branch, second.Branch);

            Run("checkout", "-q", "-b", "other");
            HexLinkRepository third = HexLinkWorkspaces.RepositoryOf(repo)!;
            Assert.Equal("other", third.Branch);
            Assert.Equal(second.Head, third.Head);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            try { Directory.Delete(repo, recursive: true); }
            catch (IOException) { }
        }

        void Run(params string[] args)
        {
            GitResult result = GitProcess.Run(repo, args);
            Assert.True(result.Ok, result.Why);
        }

        void Commit(string message) =>
            Run("-c", "user.name=Link test", "-c", "user.email=link-test@example.invalid", "commit", "-qm", message);
    }
}
