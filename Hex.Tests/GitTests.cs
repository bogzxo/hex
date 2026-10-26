using Hex.Core;
using Hex.Core.Git;

using Horizon.Core.Platform;

namespace Hex.Tests;

/// <summary>
/// What Hex makes of git, status, the lines that changed, blame and branches. Read out of git's own words first, then
/// against a real repository made for the test, when there's a git to make one with.
/// </summary>
public sealed class GitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-git-" + Guid.NewGuid().ToString("N"));

    public GitTests() => Directory.CreateDirectory(root);

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

    /* Status */

    [Fact]
    public void Status_reads_the_branch_and_every_kind_of_change()
    {
        string porcelain = string.Join('\0',
            "# branch.oid 1234567890abcdef1234567890abcdef12345678",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaa bbb src/changed file.cs",
            "1 A. N... 000000 100644 100644 000 ccc src/new.cs",
            "1 D. N... 100644 000000 000000 ddd 000 gone.txt",
            "2 R. N... 100644 100644 100644 eee eee R100 src/moved.cs",
            "src/old.cs",
            "u UU N... 100644 100644 100644 100644 fff ggg hhh both.txt",
            "? notes/todo.md",
            "! bin/",
            "! debug.log",
            string.Empty);

        GitStatus status = GitStatus.Parse(root, porcelain);

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal((2, 1), (status.Ahead, status.Behind));
        Assert.Equal(new GitFileState(GitChange.Modified, false, true), status.StateOf(At("src/changed file.cs")));
        Assert.Equal(GitChange.Added, status.StateOf(At("src/new.cs")).Change);
        Assert.True(status.StateOf(At("src/new.cs")).Staged);
        Assert.Equal(GitChange.Deleted, status.StateOf(At("gone.txt")).Change);
        Assert.Equal(GitChange.Renamed, status.StateOf(At("src/moved.cs")).Change);
        Assert.Equal(At("src/old.cs"), status.StateOf(At("src/moved.cs")).From);
        Assert.Equal(GitChange.Conflicted, status.StateOf(At("both.txt")).Change);
        Assert.Equal(GitChange.Untracked, status.StateOf(At("notes/todo.md")).Change);
        Assert.Equal(GitChange.Ignored, status.StateOf(At("bin/Debug/app.dll")).Change);
        Assert.Equal(GitChange.Ignored, status.StateOf(At("debug.log")).Change);
        Assert.Equal(GitChange.None, status.StateOf(At("src/same.cs")).Change);

        // A folder is the strongest change in it, the root has the conflict
        Assert.Equal(GitChange.Modified, status.FolderChange(At("src")));
        Assert.Equal(GitChange.Untracked, status.FolderChange(At("notes")));
        Assert.Equal(GitChange.Conflicted, status.FolderChange(root));
        Assert.Equal(GitChange.Ignored, status.FolderChange(At("bin")));
    }

    [Fact]
    public void A_detached_head_or_an_empty_repository_has_no_branch_or_commit()
    {
        GitStatus status = GitStatus.Parse(root, "# branch.oid (initial)\0# branch.head (detached)\0");
        Assert.Null(status.Branch);
        Assert.Null(status.Commit);
        Assert.Empty(status.Files);
    }

    [Fact]
    public void A_submodule_is_told_from_a_file_with_what_moved_in_it()
    {
        string porcelain = string.Join('\0',
            "# branch.head main",
            "1 .M SC.. 160000 160000 160000 aaa aaa engine",
            "1 .M S.MU 160000 160000 160000 bbb bbb tools/hidl",
            "1 M. SC.. 160000 160000 160000 ccc ddd staged module",
            "1 .M N... 100644 100644 100644 eee eee src/plain.cs",
            string.Empty);

        GitStatus status = GitStatus.Parse(root, porcelain);

        Assert.Equal(GitSubmodule.Module | GitSubmodule.Commits, status.StateOf(At("engine")).Submodule);
        Assert.Equal(GitSubmodule.Module | GitSubmodule.Modified | GitSubmodule.Untracked, status.StateOf(At("tools/hidl")).Submodule);
        Assert.True(status.StateOf(At("staged module")).Staged);
        Assert.True(status.StateOf(At("staged module")).IsRepository);
        Assert.Equal(GitSubmodule.None, status.StateOf(At("src/plain.cs")).Submodule);
        Assert.False(status.StateOf(At("src/plain.cs")).IsRepository);

        // A submodule is a folder, so it takes its own change as well as giving it to the folders above
        Assert.Equal(GitChange.Modified, status.FolderChange(At("tools/hidl")));
        Assert.Equal(GitChange.Modified, status.FolderChange(At("tools")));
    }

    [Fact]
    public void A_repository_nobody_added_is_one_folder_with_a_name()
    {
        string porcelain = string.Join('\0', "# branch.head main", "? game/", "? notes.md", string.Empty);

        GitStatus status = GitStatus.Parse(root, porcelain);

        // Kept without the slash on the end, so its name is the folder's and not empty
        Assert.True(status.Files.ContainsKey(At("game")));
        Assert.Equal("game", Path.GetFileName(status.Files.Keys.First(path => status.Files[path].IsRepository)));
        Assert.Equal(new GitFileState(GitChange.Untracked, false, true, Submodule: GitSubmodule.Nested), status.StateOf(At("game")));
        Assert.Equal(GitChange.Untracked, status.FolderChange(At("game")));
        Assert.Equal(GitSubmodule.None, status.StateOf(At("notes.md")).Submodule);
    }

    /* The lines that changed */

    [Theory]
    [InlineData("a b c", "a b c", "")]
    [InlineData("a b c", "a x b c", "1+0 1:1")]
    [InlineData("a b c", "a c", "1:1 1+0")]
    [InlineData("a b c", "a x c", "1:1 1:1")]
    [InlineData("a b c d e", "x b c y e", "0:1 0:1,3:1 3:1")]
    [InlineData("", "a b", "0:1 0:2")]
    public void The_diff_finds_lines_added_taken_out_and_changed(string before, string after, string expected)
    {
        string[] Split(string text) => text.Length == 0 ? [string.Empty] : text.Split(' ');
        List<GitHunk> hunks = GitLineDiff.Diff(Split(before), Split(after));

        // old start:count new start:count, an added run's old side written as start+0
        string written = string.Join(',', hunks.Select(h => $"{h.OldStart}{(h.OldCount == 0 ? "+0" : $":{h.OldCount}")} {h.NewStart}{(h.NewCount == 0 ? "+0" : $":{h.NewCount}")}"));
        Assert.Equal(expected, written);
    }

    [Fact]
    public void The_diff_of_a_big_file_with_scattered_changes_is_quick_and_right()
    {
        var before = Enumerable.Range(0, 20000).Select(i => $"line {i}").ToArray();
        var after = before.ToList();
        for (int i = 19000; i >= 0; i -= 1000)
            after[i] = $"changed {i}";
        after.Insert(500, "new line");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        List<GitHunk> hunks = GitLineDiff.Diff(before, after);
        timer.Stop();

        Assert.Equal(21, hunks.Count);
        Assert.Contains(hunks, hunk => hunk.IsAdded && hunk.NewStart == 500);
        Assert.True(timer.ElapsedMilliseconds < 200, $"took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_rewrite_of_most_of_a_big_file_is_still_told_apart_and_not_one_block()
    {
        // Every other line of twenty thousand changed, far past what Myers is let do, so patience takes over
        var before = Enumerable.Range(0, 20000).Select(i => $"line {i}").ToArray();
        var after = before.Select((line, i) => i % 2 == 0 ? $"changed {i}" : line).ToArray();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        List<GitHunk> hunks = GitLineDiff.Diff(before, after);
        timer.Stop();

        Assert.Equal(10000, hunks.Count);
        Assert.All(hunks, hunk => Assert.Equal((1, 1), (hunk.OldCount, hunk.NewCount)));
        Assert.True(timer.ElapsedMilliseconds < 1000, $"took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_line_typed_on_the_empty_last_line_is_added_not_changed()
    {
        List<GitHunk> hunks = GitLineDiff.Diff(GitLineDiff.Lines("a\nb\n"), GitLineDiff.Lines("a\nb\nc"));
        Assert.Equal([new GitHunk(2, 0, 2, 1)], hunks);
    }

    [Fact]
    public void A_diffs_text_is_the_new_file_with_what_went_put_back_above_what_replaced_it()
    {
        GitDiffText diff = GitDiffText.Of("one\ntwo\nthree\nfour\n", "one\n2\nthree\nfour\nfive\n");

        Assert.Equal("one\ntwo\n2\nthree\nfour\nfive", diff.Text);
        Assert.Equal([new GitDiffRun(1, 1, false), new GitDiffRun(2, 1, true), new GitDiffRun(5, 1, true)], diff.Runs);
        Assert.Equal([1, 5], diff.Changes);
        Assert.Equal([0, 2, 3, 4, 5], diff.NewLines);
    }

    [Fact]
    public void A_new_file_is_all_added_and_a_gone_one_all_taken_out()
    {
        Assert.Equal([new GitDiffRun(0, 2, true)], GitDiffText.Of(string.Empty, "a\nb\n").Runs);
        Assert.Equal([new GitDiffRun(0, 2, false)], GitDiffText.Of("a\nb\n", string.Empty).Runs);
        Assert.Empty(GitDiffText.Of("same\n", "same\n").Runs);
    }

    [Fact]
    public void A_files_history_follows_it_through_a_rename()
    {
        if (!GitProcess.Installed)
            return;

        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Tester");
        Git("config", "user.email", "tester@example.com");
        File.WriteAllText(At("old.txt"), "one\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "Made it");
        Git("mv", "old.txt", "new.txt");
        Git("commit", "-q", "-m", "Renamed it");
        File.WriteAllText(At("new.txt"), "one\ntwo\n");
        Git("commit", "-q", "-am", "Grew it");

        GitRepository repository = GitRepository.Find(root)!;
        IReadOnlyList<GitCommitOfFile> log = repository.FileLog(At("new.txt"));

        Assert.Equal(["Grew it", "Renamed it", "Made it"], log.Select(commit => commit.Subject));
        Assert.Equal(["new.txt", "new.txt", "old.txt"], log.Select(commit => commit.Path));
        Assert.Equal("one\n", repository.AtCommitPath(log[2].Path, log[2].Hash)!.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Local_history_keeps_a_copy_a_save_skips_the_same_again_and_lets_the_oldest_go()
    {
        string store = At("history"), file = At("thing.txt");
        var start = new DateTime(2026, 1, 1, 12, 0, 0);
        HexLocalHistory.Keep(store, file, "one", start);
        HexLocalHistory.Keep(store, file, "one", start.AddMinutes(1));
        HexLocalHistory.Keep(store, file, "two", start.AddMinutes(2));

        List<HexKeptVersion> kept = HexLocalHistory.Versions(store, file);
        Assert.Equal(2, kept.Count);
        Assert.Equal("two", File.ReadAllText(kept[0].Copy));

        for (int i = 0; i < HexLocalHistory.MOST_VERSIONS + 5; i++)
            HexLocalHistory.Keep(store, file, $"v{i}", start.AddMinutes(3 + i));
        Assert.Equal(HexLocalHistory.MOST_VERSIONS, HexLocalHistory.Versions(store, file).Count);

        HexLocalHistory.Keep(store, file, "much later", start.AddDays(40));
        Assert.Single(HexLocalHistory.Versions(store, file));
    }

    [Fact]
    public void Local_history_skips_a_huge_file_and_lets_the_oldest_of_any_go_past_the_total()
    {
        string store = At("capped"), one = At("one.txt"), two = At("two.txt");
        var start = new DateTime(2026, 1, 1, 12, 0, 0);
        HexLocalHistory.Keep(store, one, new string('x', HexLocalHistory.MOST_CHARACTERS + 1), start);
        Assert.False(HexLocalHistory.Has(store, one));

        // Each copy a thousand bytes, room for two and a half
        HexLocalHistory.Keep(store, one, new string('a', 1000), start, mostBytes: 2500);
        HexLocalHistory.Keep(store, two, new string('b', 1000), start.AddMinutes(1), mostBytes: 2500);
        HexLocalHistory.Keep(store, one, new string('c', 1000), start.AddMinutes(2), mostBytes: 2500);

        Assert.Equal(["c"], HexLocalHistory.Versions(store, one).Select(version => File.ReadAllText(version.Copy)[..1]));
        Assert.True(HexLocalHistory.Has(store, two));
    }

    [Fact]
    public void Conflicts_are_found_whole_and_settled_either_way()
    {
        string[] lines =
        [
            "before",
            "<<<<<<< HEAD",
            "ours",
            "||||||| base",
            "ancestor",
            "=======",
            "theirs",
            ">>>>>>> feature",
            "between",
            "<<<<<<< HEAD",
            "mine",
            "=======",
            ">>>>>>> other",
            "======= not a marker, words after it",
            "after"
        ];

        List<GitConflict> found = GitConflicts.Find(lines);
        Assert.Equal([new GitConflict(1, 3, 5, 7), new GitConflict(9, -1, 11, 12)], found);

        Assert.Equal(["before", "ours", "between"], GitConflicts.Settle(lines, found[0], GitConflictPick.Current).Take(3));
        Assert.Equal(["before", "theirs", "between"], GitConflicts.Settle(lines, found[0], GitConflictPick.Incoming).Take(3));
        Assert.Equal(["before", "ours", "theirs", "between"], GitConflicts.Settle(lines, found[0], GitConflictPick.Both).Take(4));
        Assert.Equal(["between", "after"], GitConflicts.Settle(lines, found[1], GitConflictPick.Incoming).Skip(8).Where(line => !line.StartsWith('=')));
    }

    /* Blame and branches */

    [Fact]
    public void Blame_reads_each_lines_commit_once_and_lines_nobody_committed()
    {
        string porcelain = string.Join('\n',
            "1111111111111111111111111111111111111111 1 1 2",
            "author Ada",
            "author-mail <ada@example.com>",
            "author-time 1700000000",
            "author-tz +0200",
            "summary First go",
            "filename a.txt",
            "\tone",
            "1111111111111111111111111111111111111111 2 2",
            "\ttwo",
            "0000000000000000000000000000000000000000 3 3 1",
            "author External file (--contents)",
            "author-mail <external.file>",
            "author-time 1700000100",
            "author-tz +0000",
            "summary Version of a.txt from a.txt",
            "\tthree",
            string.Empty);

        IReadOnlyList<GitBlameLine> lines = GitBlame.Parse(porcelain);

        Assert.Equal(3, lines.Count);
        Assert.Equal("Ada", lines[1].Author);
        Assert.Equal("First go", lines[1].Summary);
        Assert.Equal("11111111", lines[0].Short);
        Assert.Equal(TimeSpan.FromHours(2), lines[0].When.Offset);
        Assert.False(lines[2].Committed);
    }

    [Fact]
    public void Branches_put_the_one_checked_out_first_and_leave_out_origin_head()
    {
        string output = string.Join('\n',
            " \0refs/heads/feature\0feature\0\02 days ago\0Try a thing",
            "*\0refs/heads/main\0main\0origin/main\03 hours ago\0Fix it",
            " \0refs/remotes/origin/HEAD\0origin\0\03 hours ago\0Fix it",
            " \0refs/remotes/origin/main\0origin/main\0\03 hours ago\0Fix it",
            string.Empty);

        IReadOnlyList<GitBranch> branches = GitBranch.Parse(output);

        Assert.Equal(["main", "feature", "origin/main"], branches.Select(branch => branch.Name));
        Assert.True(branches[0].Current);
        Assert.Equal("origin/main", branches[0].Upstream);
        Assert.True(branches[2].Remote);
    }

    /* A real repository */

    private void Git(params string[] arguments)
    {
        GitResult result = GitProcess.Run(root, arguments);
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} said {result.Why}");
    }

    [Fact]
    public void A_real_repository_is_found_and_read()
    {
        if (!GitProcess.Installed)
            return;

        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Tester");
        Git("config", "user.email", "tester@example.com");
        File.WriteAllText(At("a.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(At(".gitignore"), "bin/\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "First go");

        Directory.CreateDirectory(At("bin"));
        File.WriteAllText(At("bin/out.dll"), "built");
        Directory.CreateDirectory(At("src"));
        File.WriteAllText(At("src/new.cs"), "class A {}");
        File.WriteAllText(At("a.txt"), "one\n2\nthree\nfour\n");

        GitRepository? repository = GitRepository.Find(At("src/new.cs"));
        Assert.NotNull(repository);
        Assert.True(PlatformPaths.Same(root, repository.Root));

        GitStatus? status = repository.Status();
        Assert.NotNull(status);
        Assert.Equal("main", status.Branch);
        Assert.Equal(GitChange.Modified, status.StateOf(At("a.txt")).Change);
        Assert.Equal(GitChange.Untracked, status.StateOf(At("src/new.cs")).Change);
        Assert.Equal(GitChange.Ignored, status.StateOf(At("bin/out.dll")).Change);

        // What the editor has against what git has, line 2 changed and line 4 added
        string staged = repository.Staged(At("a.txt"))!;
        List<GitHunk> hunks = GitLineDiff.Diff(GitLineDiff.Lines(staged), GitLineDiff.Lines(File.ReadAllText(At("a.txt"))));
        Assert.Equal([new GitHunk(1, 1, 1, 1), new GitHunk(3, 0, 3, 1)], hunks);

        // The editor's text, a line typed that was never saved is nobody's yet
        IReadOnlyList<GitBlameLine> blame = repository.Blame(At("a.txt"), "one\n2\nthree\nfour\n");
        Assert.Equal("Tester", blame[0].Author);
        Assert.False(blame[1].Committed);

        // Staged, unstaged, thrown away and committed
        Assert.Null(repository.Stage([At("src/new.cs")]));
        Assert.True(repository.Status()!.StateOf(At("src/new.cs")) is { Change: GitChange.Added, Staged: true });
        Assert.Null(repository.Unstage([At("src/new.cs")]));
        Assert.Equal(GitChange.Untracked, repository.Status()!.StateOf(At("src/new.cs")).Change);
        Assert.Null(repository.Discard([At("a.txt")]));
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(At("a.txt")).Replace("\r\n", "\n"));
        Assert.Null(repository.Stage([At("src/new.cs")]));
        Assert.Null(repository.Commit("Add the new one\n\nWith a body as well"));
        Assert.Equal(GitChange.None, repository.Status()!.StateOf(At("src/new.cs")).Change);
        Assert.NotNull(repository.Commit("nothing staged"));

        Assert.Null(repository.CreateBranch("feature"));
        Assert.Equal("feature", repository.Status()!.Branch);
        Assert.Contains(repository.Branches(), branch => branch.Name == "main" && !branch.Current);
        Assert.Null(repository.Switch(repository.Branches().First(branch => branch.Name == "main")));
        Assert.Equal("main", repository.Status()!.Branch);
    }
}
