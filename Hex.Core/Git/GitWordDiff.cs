namespace Hex.Core.Git;

/// <summary>A piece of a line of a diff's text that changed inside a changed line, a word taken out or put in.</summary>
/// <param name="Line">The line of the diff's text it's on.</param>
/// <param name="Column">Where on that line it starts.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="Added">Whether it was put in (on a line of the new version) rather than taken out.</param>
internal readonly record struct GitDiffWord(int Line, int Column, int Length, bool Added);

/// <summary>
/// The words that changed between the lines a diff took out and the ones it put in for them, so a line that only had a
/// number changed shows the number and not the whole line. Each line taken out goes with the line put in at the same
/// place after it, and the two are cut into words, spaces and marks and those diffed the way lines are.
/// </summary>
internal static class GitWordDiff
{
    // Past this long a line is only told apart by what's the same at either end, a minified file's one line was a
    // diff of tens of thousands of words
    private const int LONGEST_LINE = 400;

    /// <summary>The words that changed in a diff's text, by the runs it took out and put in, in order.</summary>
    public static List<GitDiffWord> Of(IReadOnlyList<string> lines, IReadOnlyList<GitDiffRun> runs)
    {
        var words = new List<GitDiffWord>();
        for (int i = 0; i + 1 < runs.Count; i++)
        {
            GitDiffRun gone = runs[i], came = runs[i + 1];
            if (gone.Added || !came.Added || came.Line != gone.Line + gone.Count)
                continue;

            for (int pair = 0; pair < Math.Min(gone.Count, came.Count); pair++)
            {
                int oldLine = gone.Line + pair, newLine = came.Line + pair;
                if (oldLine >= lines.Count || newLine >= lines.Count)
                    break;

                var (taken, put) = Changes(lines[oldLine], lines[newLine]);
                foreach (var (column, length) in taken)
                    words.Add(new GitDiffWord(oldLine, column, length, Added: false));
                foreach (var (column, length) in put)
                    words.Add(new GitDiffWord(newLine, column, length, Added: true));
            }
        }

        return words;
    }

    /// <summary>What of one line was taken out and what of the other put in for it, as where each piece starts and how long it is.</summary>
    public static (List<(int Column, int Length)> Gone, List<(int Column, int Length)> Came) Changes(string before, string after)
    {
        if (before.Length > LONGEST_LINE || after.Length > LONGEST_LINE)
            return Ends(before, after);

        var (oldWords, oldStarts) = Words(before);
        var (newWords, newStarts) = Words(after);
        var gone = new List<(int, int)>();
        var came = new List<(int, int)>();
        foreach (GitHunk hunk in GitLineDiff.Diff(oldWords, newWords))
        {
            if (hunk.OldCount > 0)
                Add(gone, oldStarts[hunk.OldStart], End(oldStarts, oldWords, hunk.OldStart + hunk.OldCount - 1));
            if (hunk.NewCount > 0)
                Add(came, newStarts[hunk.NewStart], End(newStarts, newWords, hunk.NewStart + hunk.NewCount - 1));
        }

        return (gone, came);
    }

    // Where the word at an index ends
    private static int End(List<int> starts, List<string> words, int index) => starts[index] + words[index].Length;

    // A piece put in after the last, the two made one when they touch
    private static void Add(List<(int Column, int Length)> pieces, int from, int to)
    {
        if (pieces.Count > 0 && pieces[^1].Column + pieces[^1].Length == from)
            pieces[^1] = (pieces[^1].Column, to - pieces[^1].Column);
        else
            pieces.Add((from, to - from));
    }

    /// <summary>
    /// Helper method to cut a line into what's diffed, a run of letters, digits and underscores is one word, a run of
    /// spaces is one, and every other character is one on its own. Where each starts too.
    /// </summary>
    private static (List<string> Words, List<int> Starts) Words(string line)
    {
        var words = new List<string>();
        var starts = new List<int>();
        int at = 0;
        while (at < line.Length)
        {
            int from = at;
            char first = line[at];
            if (IsWord(first))
                while (at < line.Length && IsWord(line[at])) at++;
            else if (char.IsWhiteSpace(first))
                while (at < line.Length && char.IsWhiteSpace(line[at])) at++;
            else
                at++;

            words.Add(line[from..at]);
            starts.Add(from);
        }

        return (words, starts);
    }

    private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>Helper method for a long line, what's between the same at its start and the same at its end on either side.</summary>
    private static (List<(int Column, int Length)> Gone, List<(int Column, int Length)> Came) Ends(string before, string after)
    {
        int start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start])
            start++;

        int end = 0;
        while (end < before.Length - start && end < after.Length - start && before[^(end + 1)] == after[^(end + 1)])
            end++;

        var gone = new List<(int, int)>();
        var came = new List<(int, int)>();
        if (before.Length - start - end > 0)
            gone.Add((start, before.Length - start - end));
        if (after.Length - start - end > 0)
            came.Add((start, after.Length - start - end));
        return (gone, came);
    }
}
