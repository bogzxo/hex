using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>
/// A checkpoint of a working copy taken before an agent's turn and files put back from it, in a repository made for the
/// test. Skipped quietly where there's no git, as the other git tests are.
/// </summary>
public sealed class HexCheckpointsTests
{
    [Fact]
    public void A_file_comes_back_as_it_was_a_new_one_goes_and_the_index_and_history_stay_as_they_were()
    {
        if (!GitProcess.Installed)
            return;

        string top = Path.Combine(Path.GetTempPath(), "hex-checkpoint-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(top);
        try
        {
            void Git(params string[] arguments) => Assert.True(GitProcess.Run(top, arguments).Ok, string.Join(' ', arguments));
            Git("init", "-q");
            Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "start");
            File.WriteAllText(Path.Combine(top, "kept.txt"), "committed\n");
            Git("add", "kept.txt");
            Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "kept");

            // Before the turn, a change not staged and a file nobody added yet
            File.WriteAllText(Path.Combine(top, "kept.txt"), "before the turn\n");
            File.WriteAllText(Path.Combine(top, "loose.txt"), "untracked before\n");
            string status = GitProcess.Run(top, ["status", "--porcelain"]).Output;
            string head = GitProcess.Run(top, ["rev-parse", "HEAD"]).Output;

            string? tree = HexCheckpoints.Take(top);
            Assert.NotNull(tree);
            Assert.Equal(status, GitProcess.Run(top, ["status", "--porcelain"]).Output);
            Assert.Equal(head, GitProcess.Run(top, ["rev-parse", "HEAD"]).Output);

            // The turn changes both and makes a new one
            File.WriteAllText(Path.Combine(top, "kept.txt"), "the agent's\n");
            File.WriteAllText(Path.Combine(top, "loose.txt"), "the agent's too\n");
            File.WriteAllText(Path.Combine(top, "new.txt"), "made in the turn\n");

            Assert.Equal("before the turn\n", HexCheckpoints.TextAt(top, tree!, Path.Combine(top, "kept.txt")));
            Assert.Null(HexCheckpoints.TextAt(top, tree!, Path.Combine(top, "new.txt")));

            Assert.Null(HexCheckpoints.Restore(top, tree!, Path.Combine(top, "kept.txt")));
            Assert.Null(HexCheckpoints.Restore(top, tree!, Path.Combine(top, "loose.txt")));
            Assert.Null(HexCheckpoints.Restore(top, tree!, Path.Combine(top, "new.txt")));
            Assert.Equal("before the turn\n", File.ReadAllText(Path.Combine(top, "kept.txt")).Replace("\r\n", "\n"));
            Assert.Equal("untracked before\n", File.ReadAllText(Path.Combine(top, "loose.txt")).Replace("\r\n", "\n"));
            Assert.False(File.Exists(Path.Combine(top, "new.txt")));
            Assert.Equal(status, GitProcess.Run(top, ["status", "--porcelain"]).Output);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(top, true);
        }
    }

    [Fact]
    public void A_folder_of_several_repositories_has_each_of_them()
    {
        if (!GitProcess.Installed)
            return;

        string outer = Path.Combine(Path.GetTempPath(), "hex-repos-" + Guid.NewGuid().ToString("N")[..8]);
        string first = Path.Combine(outer, "first"), second = Path.Combine(outer, "group", "second"), built = Path.Combine(outer, "bin", "built");
        foreach (string folder in new[] { first, second, built })
        {
            Directory.CreateDirectory(folder);
            Assert.True(GitProcess.Run(folder, ["init", "-q"]).Ok);
        }

        try
        {
            // Not a repository itself, the two below it are and the one in bin is left out
            Assert.Equal([first, second], HexCheckpoints.ReposOf(outer).Select(Path.GetFullPath).Order().ToArray());

            // Inside one, it's just that one
            Directory.CreateDirectory(Path.Combine(first, "deeper"));
            Assert.Equal([first], HexCheckpoints.ReposOf(Path.Combine(first, "deeper")).Select(Path.GetFullPath).ToArray());
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(outer, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(outer, true);
        }
    }
}
