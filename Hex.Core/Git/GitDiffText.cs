namespace Hex.Core.Git;

/// <summary>A run of lines of a diff's text, from a line for so many, put in (added) or taken out.</summary>
internal readonly record struct GitDiffRun(int Line, int Count, bool Added);

/// <summary>
/// The whole of a file as it is now with what was taken out of it put back in where it was, above what replaced it, the
/// way VS Code's inline diff reads. Every line of both versions is there once, the ones that are the same once for both.
/// </summary>
/// <param name="Text">The lines, the way an editor shows them.</param>
/// <param name="Runs">Which of its lines were taken out and which put in, in order.</param>
/// <param name="Changes">The line each change starts on, for going from one to the next.</param>
/// <param name="NewLines">Where each line of the new version is in the text, by the new version's line, to land on the same place.</param>
internal sealed record GitDiffText(string Text, IReadOnlyList<GitDiffRun> Runs, IReadOnlyList<int> Changes, int[] NewLines)
{
    /// <summary>The words that changed inside the lines that changed, each line taken out against the one put in for it.</summary>
    public IReadOnlyList<GitDiffWord> Words { get; init; } = [];

    /// <summary>The text of a diff of two versions, a file of nothing for either side for one that's new or gone.</summary>
    public static GitDiffText Of(string before, string after)
    {
        string[] old = GitLineDiff.Lines(before), now = GitLineDiff.Lines(after);
        if (before.Length == 0) old = [];
        if (after.Length == 0) now = [];

        List<GitHunk> hunks = GitLineDiff.Diff(old, now);
        string ending = after.Contains("\r\n", StringComparison.Ordinal) || (after.Length == 0 && before.Contains("\r\n", StringComparison.Ordinal)) ? "\r\n" : "\n";

        var lines = new List<string>(old.Length + now.Length);
        var runs = new List<GitDiffRun>();
        var changes = new List<int>();
        var newLines = new int[now.Length];
        int oldAt = 0, newAt = 0;

        void Same(int upToNew)
        {
            while (newAt < upToNew)
            {
                newLines[newAt++] = lines.Count;
                lines.Add(old[oldAt++]);
            }
        }

        foreach (GitHunk hunk in hunks)
        {
            Same(hunk.NewStart);
            changes.Add(lines.Count);

            if (hunk.OldCount > 0)
            {
                runs.Add(new GitDiffRun(lines.Count, hunk.OldCount, Added: false));
                for (int i = 0; i < hunk.OldCount; i++)
                    lines.Add(old[oldAt++]);
            }

            if (hunk.NewCount > 0)
            {
                runs.Add(new GitDiffRun(lines.Count, hunk.NewCount, Added: true));
                for (int i = 0; i < hunk.NewCount; i++)
                {
                    newLines[newAt++] = lines.Count;
                    lines.Add(now[newAt - 1]);
                }
            }
        }

        Same(now.Length);
        return new GitDiffText(string.Join(ending, lines), runs, changes, newLines) { Words = GitWordDiff.Of(lines, runs) };
    }

    /// <summary>
    /// Every line's number in the old version and in the new one, counted from 1, 0 where it isn't in that one (a line
    /// taken out has no new number, one put in no old one). The two columns of numbers down a diff, VS Code's.
    /// </summary>
    public (int[] Old, int[] New) Numbers()
    {
        int count = NewLines.Length;
        foreach (GitDiffRun run in Runs)
        {
            if (!run.Added)
                count += run.Count;
        }

        var olds = new int[count];
        var news = new int[count];
        var gone = new bool[count];
        var came = new bool[count];
        foreach (GitDiffRun run in Runs)
        {
            for (int line = run.Line; line < run.Line + run.Count && line < count; line++)
            {
                if (run.Added) came[line] = true;
                else gone[line] = true;
            }
        }

        int oldAt = 0, newAt = 0;
        for (int line = 0; line < count; line++)
        {
            olds[line] = came[line] ? 0 : ++oldAt;
            news[line] = gone[line] ? 0 : ++newAt;
        }

        return (olds, news);
    }
}
