using System.Text;

using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>
/// Equivalence, edge-case, coalescing, and allocation tests for the Span-based Git parsers and query coalescing in
/// <c>Hex.Core.Git</c>.
/// </summary>
public sealed class GitPerfTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-git-perf-" + Guid.NewGuid().ToString("N"));

    private string At(string relative) => Path.GetFullPath(Path.Combine(root, relative));

    [Fact]
    public void Porcelain_v2_and_v1_status_parse_equivalently_including_renames_spaces_utf8_and_conflicts()
    {
        string v2 = string.Join('\0',
            "# branch.oid 1234567890abcdef1234567890abcdef12345678",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +3 -2",
            "1 .M N... 100644 100644 100644 aaa bbb src/changed file.cs",
            "1 A. N... 000000 100644 100644 000 ccc src/café.cs",
            "1 D. N... 100644 000000 000000 ddd 000 gone.txt",
            "2 R. N... 100644 100644 100644 eee eee R100 src/new name.cs",
            "src/old name.cs",
            "u UU N... 100644 100644 100644 100644 fff ggg hhh both.txt",
            "? notes/todo.md",
            "? nested/",
            "! bin/",
            "! debug.log",
            string.Empty);

        string v1Line = string.Join("\r\n",
            "## main...origin/main [ahead 3, behind 2]",
            " M src/changed file.cs",
            "A  \"src/caf\\303\\251.cs\"",
            "D  gone.txt",
            "R  \"src/old name.cs\" -> \"src/new name.cs\"",
            "UU both.txt",
            "?? notes/todo.md",
            "?? nested/",
            "!! bin/",
            "!! debug.log",
            string.Empty);

        string v1Nul = string.Join('\0',
            "## main...origin/main [ahead 3, behind 2]",
            " M src/changed file.cs",
            "A  src/café.cs",
            "D  gone.txt",
            "R  src/new name.cs",
            "src/old name.cs",
            "UU both.txt",
            "?? notes/todo.md",
            "?? nested/",
            "!! bin/",
            "!! debug.log",
            string.Empty);

        GitStatus statusV2 = GitStatus.Parse(root, v2);
        GitStatus statusV1Line = GitStatus.Parse(root, v1Line);
        GitStatus statusV1Nul = GitStatus.Parse(root, v1Nul);

        foreach (GitStatus status in new[] { statusV2, statusV1Line, statusV1Nul })
        {
            Assert.Equal("main", status.Branch);
            Assert.Equal("origin/main", status.Upstream);
            Assert.Equal((3, 2), (status.Ahead, status.Behind));
            Assert.Equal(new GitFileState(GitChange.Modified, false, true), status.StateOf(At("src/changed file.cs")));
            Assert.Equal(new GitFileState(GitChange.Added, true, false), status.StateOf(At("src/café.cs")));
            Assert.Equal(new GitFileState(GitChange.Deleted, true, false), status.StateOf(At("gone.txt")));
            Assert.Equal(new GitFileState(GitChange.Renamed, true, false, At("src/old name.cs")), status.StateOf(At("src/new name.cs")));
            Assert.Equal(GitChange.Conflicted, status.StateOf(At("both.txt")).Change);
            Assert.Equal(GitChange.Untracked, status.StateOf(At("notes/todo.md")).Change);
            Assert.Equal(new GitFileState(GitChange.Untracked, false, true, Submodule: GitSubmodule.Nested), status.StateOf(At("nested")));
            Assert.Equal(GitChange.Ignored, status.StateOf(At("bin/Debug/app.dll")).Change);
            Assert.Equal(GitChange.Ignored, status.FolderChange(At("bin")));
            Assert.Equal(GitChange.Ignored, status.StateOf(At("debug.log")).Change);
            Assert.Equal(GitChange.None, status.StateOf(At("src/clean.cs")).Change);
            Assert.Equal(GitChange.Conflicted, status.FolderChange(root));
        }
    }

    [Fact]
    public void UnescapeQuotedPath_decodes_utf8_octal_and_c_escapes()
    {
        Assert.Equal("café.txt", GitStatus.UnescapeQuotedPath("\"caf\\303\\251.txt\""));
        Assert.Equal("dir/my \"quoted\" \\file.cs", GitStatus.UnescapeQuotedPath("\"dir/my \\\"quoted\\\" \\\\file.cs\""));
        Assert.Equal("a\tb.txt", GitStatus.UnescapeQuotedPath("\"a\\tb.txt\""));
        Assert.Equal("plain.cs", GitStatus.UnescapeQuotedPath("plain.cs"));
    }

    [Fact]
    public void GitBlame_span_parser_handles_crlf_sha256_and_reuses_commit_and_author_strings()
    {
        string hash1 = "1111111111111111111111111111111111111111";
        string hash2 = "2222222222222222222222222222222222222222222222222222222222222222";
        string zero = new('0', 40);

        string porcelain = string.Join("\r\n",
            $"{hash1} 1 1 2",
            "author Ada Lovelace",
            "author-mail <ada@example.com>",
            "author-time 1700000000",
            "author-tz +0200",
            "summary First commit",
            "filename a.txt",
            "\tline one",
            $"{hash1} 2 2",
            "\tline two",
            $"{hash2} 3 3 1",
            "author Ada Lovelace",
            "author-mail <ada@example.com>",
            "author-time 1700000060",
            "author-tz -0530",
            "summary Second commit",
            "\tline three",
            $"{zero} 4 4 1",
            "author Not Committed Yet",
            "author-mail <not.committed.yet>",
            "author-time 1700000120",
            "author-tz +0000",
            "summary Working tree",
            "\tline four",
            string.Empty);

        IReadOnlyList<GitBlameLine> lines = GitBlame.Parse(porcelain);

        Assert.Equal(4, lines.Count);
        Assert.Same(lines[0], lines[1]);
        Assert.Same(lines[0].Author, lines[2].Author);
        Assert.Same(lines[0].Mail, lines[2].Mail);
        Assert.Equal("ada@example.com", lines[0].Mail);
        Assert.Equal(TimeSpan.FromHours(2), lines[0].When.Offset);
        Assert.Equal(TimeSpan.FromMinutes(-330), lines[2].When.Offset);
        Assert.Equal("22222222", lines[2].Short);
        Assert.False(lines[3].Committed);
        Assert.Null(lines[3].Commit);
    }

    [Fact]
    public void GitBranch_span_parser_orders_current_local_remote_and_handles_crlf()
    {
        string output = string.Join("\r\n",
            " \0refs/remotes/origin/feature\0origin/feature\0\01 hour ago\0Remote feature",
            " \0refs/heads/beta\0beta\0\02 hours ago\0Beta work",
            " \0refs/remotes/origin/HEAD\0origin\0\03 hours ago\0Pointer",
            "*\0refs/heads/main\0main\0origin/main\03 hours ago\0Main commit",
            " \0refs/heads/alpha\0alpha\0origin/alpha\01 day ago\0Alpha work",
            " \0refs/remotes/origin/main\0origin/main\0\03 hours ago\0Main commit",
            string.Empty);

        IReadOnlyList<GitBranch> branches = GitBranch.Parse(output);

        Assert.Equal(["main", "beta", "alpha", "origin/feature", "origin/main"], branches.Select(b => b.Name));
        Assert.True(branches[0].Current);
        Assert.Equal("origin/main", branches[0].Upstream);
        Assert.Null(branches[1].Upstream);
        Assert.False(branches[1].Remote);
        Assert.True(branches[3].Remote);
    }

    [Fact]
    public void GitStash_and_GitUnmerged_span_parsers_preserve_exact_records()
    {
        string stashOut = "stash@{0}\0On main: wip one\01760000000\r\nstash@{2}\0WIP on dev: abc\01750000000\r\n";
        IReadOnlyList<GitStashEntry> stashes = GitStashEntry.Parse(stashOut);

        Assert.Equal([0, 2], stashes.Select(s => s.Index));
        Assert.Equal("wip one", stashes[0].Short);
        Assert.Equal("WIP on dev: abc", stashes[1].Short);

        string unmergedOut = string.Join('\0',
            "100644 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1\tdir/file [1].txt",
            "100644 bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb 2\tdir/file [1].txt",
            "100755 cccccccccccccccccccccccccccccccccccccccc 3\tdir/file [1].txt",
            "100644 dddddddddddddddddddddddddddddddddddddddd 2\tother.txt",
            string.Empty);

        IReadOnlyList<GitUnmergedFile> all = GitUnmerged.ParseStages(unmergedOut);
        Assert.Equal(2, all.Count);
        GitUnmergedFile first = all.Single(f => f.Path == "dir/file [1].txt");
        Assert.Equal("100644", first.Base!.Mode);
        Assert.Same(first.Base.Mode, first.Mine!.Mode);
        Assert.Equal("100755", first.Theirs!.Mode);

        GitUnmergedFile filtered = Assert.Single(GitUnmerged.ParseStages(unmergedOut, "other.txt"));
        Assert.Equal("other.txt", filtered.Path);
        Assert.Null(filtered.Base);
        Assert.NotNull(filtered.Mine);
        Assert.Null(filtered.Theirs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("single")]
    [InlineData("a\n")]
    [InlineData("a\r\n")]
    [InlineData("a\nb\n")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\nb")]
    [InlineData("a\r\r\n")]
    [InlineData("a\rb")]
    [InlineData("a\n\n")]
    public void GitLineDiff_Lines_matches_legacy_replace_split_exactly(string text)
    {
        string[] legacy = LegacyLines(text);
        string[] spanLines = GitLineDiff.Lines(text);

        Assert.Equal(legacy, spanLines);
    }

    [Fact]
    public void RunCoalesced_deduplicates_concurrent_queries_and_invalidation_prevents_stale_results()
    {
        var table = new Dictionary<string, Task<int>>(StringComparer.OrdinalIgnoreCase);
        string repoRoot = root;
        int executions = 0;

        using var allStarted = new CountdownEvent(4);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        Task<int>[] concurrent =
        [
            .. Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
            {
                allStarted.Signal();
                allStarted.Wait(TimeSpan.FromSeconds(5));
                return GitRepository.RunCoalesced(table, repoRoot, () =>
                {
                    int n = Interlocked.Increment(ref executions);
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return n;
                });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
        ];

        Assert.True(allStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        // Give the other 3 dedicated threads a moment to attach to the in-flight leader before releasing it
        Thread.Sleep(50);
        release.Set();

        int[] results = Task.WhenAll(concurrent).GetAwaiter().GetResult();
        Assert.Equal(1, executions);
        Assert.All(results, r => Assert.Equal(1, r));

        // Sequential call after completion must NOT be cached
        int sequential = GitRepository.RunCoalesced(table, repoRoot, () => Interlocked.Increment(ref executions));
        Assert.Equal(2, sequential);
        Assert.Equal(2, executions);

        // Invalidation while in-flight must detach the stale query so a new caller runs fresh
        entered.Reset();
        release.Reset();
        Task<int> staleLeader = Task.Run(() =>
            GitRepository.RunCoalesced(table, repoRoot, () =>
            {
                int n = Interlocked.Increment(ref executions);
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return n;
            }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        table.Remove(repoRoot);
        int postMutation = GitRepository.RunCoalesced(table, repoRoot, () => Interlocked.Increment(ref executions));
        release.Set();
        int staleResult = staleLeader.GetAwaiter().GetResult();

        Assert.Equal(3, staleResult);
        Assert.Equal(4, postMutation);
    }

    [Fact]
    public void Span_parsers_reduce_allocations_compared_to_split_baselines()
    {
        // Build representative payloads
        string blamePayload = BuildBlamePayload(lineCount: 400, commitCount: 5);
        string branchPayload = BuildBranchPayload(branchCount: 200);
        string crlfText = string.Join("\r\n", Enumerable.Range(0, 1000).Select(i => $"public int Field{i} => {i};")) + "\r\n";

        // Warm up JIT and static caches
        _ = GitBlame.Parse(blamePayload);
        _ = LegacyBlameParse(blamePayload);
        _ = GitBranch.Parse(branchPayload);
        _ = LegacyBranchParse(branchPayload);
        _ = GitLineDiff.Lines(crlfText);
        _ = LegacyLines(crlfText);

        long blameSpanBytes = MeasureAllocatedBytes(() => GitBlame.Parse(blamePayload));
        long blameLegacyBytes = MeasureAllocatedBytes(() => LegacyBlameParse(blamePayload));
        Assert.True(blameSpanBytes < blameLegacyBytes / 2, $"Expected GitBlame.Parse ({blameSpanBytes} B) < 50% of legacy ({blameLegacyBytes} B)");

        long branchSpanBytes = MeasureAllocatedBytes(() => GitBranch.Parse(branchPayload));
        long branchLegacyBytes = MeasureAllocatedBytes(() => LegacyBranchParse(branchPayload));
        Assert.True(branchSpanBytes < branchLegacyBytes, $"Expected GitBranch.Parse ({branchSpanBytes} B) < legacy ({branchLegacyBytes} B)");

        long linesSpanBytes = MeasureAllocatedBytes(() => GitLineDiff.Lines(crlfText));
        long linesLegacyBytes = MeasureAllocatedBytes(() => LegacyLines(crlfText));
        Assert.True(linesSpanBytes < linesLegacyBytes, $"Expected GitLineDiff.Lines ({linesSpanBytes} B) < legacy ({linesLegacyBytes} B)");
    }

    private static long MeasureAllocatedBytes(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string[] LegacyLines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static IReadOnlyList<GitBlameLine> LegacyBlameParse(string porcelain)
    {
        var lines = new List<GitBlameLine>();
        var commits = new Dictionary<string, GitBlameLine>(StringComparer.Ordinal);
        string? hash = null;
        string author = string.Empty, mail = string.Empty, summary = string.Empty;
        long time = 0;

        foreach (string raw in porcelain.Split('\n'))
        {
            if (raw.Length == 0)
                continue;
            if (raw[0] == '\t')
            {
                if (hash is null) continue;
                if (!commits.TryGetValue(hash, out GitBlameLine? line))
                {
                    line = new GitBlameLine(hash, author, mail, DateTimeOffset.FromUnixTimeSeconds(time), summary);
                    commits[hash] = line;
                }
                lines.Add(line);
                continue;
            }
            int space = raw.IndexOf(' ');
            string key = space < 0 ? raw : raw[..space];
            string value = space < 0 ? string.Empty : raw[(space + 1)..];
            if (key.Length == 40)
            {
                hash = key;
                continue;
            }
            switch (key)
            {
                case "author": author = value; break;
                case "author-mail": mail = value.Trim('<', '>'); break;
                case "author-time": long.TryParse(value, out time); break;
                case "summary": summary = value; break;
            }
        }
        return lines;
    }

    private static IReadOnlyList<GitBranch> LegacyBranchParse(string output)
    {
        var branches = new List<GitBranch>();
        foreach (string line in output.Split('\n'))
        {
            string[] fields = line.Split('\0');
            if (fields.Length < 6 || fields[1].EndsWith("/HEAD", StringComparison.Ordinal))
                continue;
            bool remote = fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal);
            branches.Add(new GitBranch(fields[2], remote, fields[0] == "*", fields[3].Length > 0 ? fields[3] : null, fields[4], fields[5]));
        }
        return [.. branches.OrderByDescending(b => b.Current).ThenBy(b => b.Remote)];
    }

    private static string BuildBlamePayload(int lineCount, int commitCount)
    {
        var sb = new StringBuilder(lineCount * 96);
        string[] hashes = [.. Enumerable.Range(0, commitCount).Select(i => $"{i:x40}")];
        var emitted = new bool[commitCount];
        for (int i = 0; i < lineCount; i++)
        {
            int c = i % commitCount;
            sb.Append(hashes[c]).Append(' ').Append(i + 1).Append(' ').Append(i + 1).Append(" 1\n");
            if (!emitted[c])
            {
                emitted[c] = true;
                sb.Append("author Dev ").Append(c).Append('\n');
                sb.Append("author-mail <dev").Append(c).Append("@example.com>\n");
                sb.Append("author-time ").Append(1700000000 + c * 60).Append('\n');
                sb.Append("author-tz +0200\n");
                sb.Append("committer Dev ").Append(c).Append('\n');
                sb.Append("committer-mail <dev").Append(c).Append("@example.com>\n");
                sb.Append("committer-time ").Append(1700000000 + c * 60).Append('\n');
                sb.Append("committer-tz +0200\n");
                sb.Append("summary Commit number ").Append(c).Append('\n');
                sb.Append("filename src/sample.cs\n");
            }
            sb.Append("\t// source line ").Append(i).Append(" with some realistic code content\n");
        }
        return sb.ToString();
    }

    private static string BuildBranchPayload(int branchCount)
    {
        var sb = new StringBuilder(branchCount * 80);
        sb.Append("*\0refs/heads/main\0main\0origin/main\01 hour ago\0Main branch\n");
        sb.Append(" \0refs/remotes/origin/HEAD\0origin\0\01 hour ago\0Main branch\n");
        for (int i = 1; i < branchCount; i++)
        {
            bool remote = i % 2 == 0;
            if (remote)
                sb.Append(" \0refs/remotes/origin/feat-").Append(i).Append("\0origin/feat-").Append(i).Append("\0\02 days ago\0Feature ").Append(i).Append('\n');
            else
                sb.Append(" \0refs/heads/feat-").Append(i).Append("\0feat-").Append(i).Append("\0origin/feat-").Append(i).Append("\02 days ago\0Feature ").Append(i).Append('\n');
        }
        return sb.ToString();
    }
}
