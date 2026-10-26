namespace Hex.Core.Git;

/// <summary>
/// A run of lines that differ between two versions of a file, where it starts in each (from 0) and how many lines it
/// covers there. None on the old side is lines added, none on the new side lines taken out, both a change.
/// </summary>
internal readonly record struct GitHunk(int OldStart, int OldCount, int NewStart, int NewCount)
{
    public bool IsAdded => OldCount == 0;

    public bool IsDeleted => NewCount == 0;
}

/// <summary>
/// The lines that differ between two versions of a text, Myers' diff the way git does it. Worked out in Hex rather than
/// asked of git because the new side is the editor's text as it's typed, which is in no file git could read, and a
/// keystroke's worth of change has to come back in a millisecond or two.
/// </summary>
internal static class GitLineDiff
{
    // Past this many lines added and taken out the middle is called one big change, Myers' memory goes as the square of it
    private const int MOST_EDITS = 3000;

    /// <summary>The runs of lines that differ, in order.</summary>
    public static List<GitHunk> Diff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var hunks = new List<GitHunk>();

        // What's the same at either end is left out first, an edit is nearly always a few lines in the middle of a file
        int start = 0, oldEnd = before.Count, newEnd = after.Count;
        while (start < oldEnd && start < newEnd && before[start] == after[start])
            start++;
        while (oldEnd > start && newEnd > start && before[oldEnd - 1] == after[newEnd - 1])
        {
            oldEnd--;
            newEnd--;
        }

        if (start == oldEnd && start == newEnd)
            return hunks;

        var same = new List<(int Old, int New)>();
        Match(before, start, oldEnd, after, start, newEnd, same, 0);

        // The gaps between the lines that are the same are the hunks
        int oldAt = start, newAt = start;
        foreach (var (oldLine, newLine) in same)
        {
            if (oldLine > oldAt || newLine > newAt)
                hunks.Add(new GitHunk(oldAt, oldLine - oldAt, newAt, newLine - newAt));
            oldAt = oldLine + 1;
            newAt = newLine + 1;
        }

        if (oldEnd > oldAt || newEnd > newAt)
            hunks.Add(new GitHunk(oldAt, oldEnd - oldAt, newAt, newEnd - newAt));
        return hunks;
    }

    /// <summary>
    /// The text's lines, \r\n or \n, the way the editor numbers them, but without the empty one after a last newline. With
    /// it, a line typed on the empty last line was that empty line changed rather than a line added.
    /// </summary>
    public static string[] Lines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    // How deep the patience splitting goes before what's left is called one change
    private const int MOST_DEPTH = 32;

    /// <summary>
    /// Helper method to add the lines that are the same in two ranges to a list, in order. Myers when the edits between them
    /// are few enough, and past that patience, the lines that come up once on each side taken as fixed points in order and
    /// the gaps between them gone into on their own. A rewrite of half a big file came out as one change from its first
    /// line to its last before.
    /// </summary>
    private static void Match(IReadOnlyList<string> a, int aFrom, int aTo, IReadOnlyList<string> b, int bFrom, int bTo, List<(int Old, int New)> same, int depth)
    {
        // The same at either end first, a gap between fixed points is often that
        while (aFrom < aTo && bFrom < bTo && a[aFrom] == b[bFrom])
            same.Add((aFrom++, bFrom++));
        var tail = new List<(int, int)>();
        while (aTo > aFrom && bTo > bFrom && a[aTo - 1] == b[bTo - 1])
            tail.Add((--aTo, --bTo));

        if (aFrom < aTo && bFrom < bTo)
        {
            if (Matches(a, aFrom, aTo - aFrom, b, bFrom, bTo - bFrom) is { } found)
                same.AddRange(found);
            else if (depth < MOST_DEPTH && Anchors(a, aFrom, aTo, b, bFrom, bTo) is { Count: > 0 } anchors)
            {
                int oldAt = aFrom, newAt = bFrom;
                foreach (var (oldLine, newLine) in anchors)
                {
                    Match(a, oldAt, oldLine, b, newAt, newLine, same, depth + 1);
                    same.Add((oldLine, newLine));
                    oldAt = oldLine + 1;
                    newAt = newLine + 1;
                }

                Match(a, oldAt, aTo, b, newAt, bTo, same, depth + 1);
            }
        }

        tail.Reverse();
        same.AddRange(tail);
    }

    /// <summary>
    /// Helper method for patience's fixed points, the lines that are there once in each range, as the longest run of them
    /// that's in the same order on both sides. Empty when there are none.
    /// </summary>
    private static List<(int Old, int New)> Anchors(IReadOnlyList<string> a, int aFrom, int aTo, IReadOnlyList<string> b, int bFrom, int bTo)
    {
        // Each line's count and where it was on either side, -1 for more than once
        var seen = new Dictionary<string, (int Old, int New)>(StringComparer.Ordinal);
        for (int i = aFrom; i < aTo; i++)
            seen[a[i]] = seen.TryGetValue(a[i], out var had) ? (-1, had.New) : (i, -2);
        for (int j = bFrom; j < bTo; j++)
        {
            if (seen.TryGetValue(b[j], out var had))
                seen[b[j]] = (had.Old, had.New == -2 ? j : -1);
        }

        var pairs = new List<(int Old, int New)>();
        foreach (var (oldLine, newLine) in seen.Values)
        {
            if (oldLine >= 0 && newLine >= 0)
                pairs.Add((oldLine, newLine));
        }

        pairs.Sort((x, y) => x.Old.CompareTo(y.Old));

        // The longest run rising on the new side too, patience sorting
        var tops = new List<int>();
        var back = new int[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            int low = 0, high = tops.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (pairs[tops[middle]].New < pairs[i].New) low = middle + 1;
                else high = middle;
            }

            back[i] = low > 0 ? tops[low - 1] : -1;
            if (low == tops.Count) tops.Add(i);
            else tops[low] = i;
        }

        var run = new List<(int, int)>();
        for (int at = tops.Count > 0 ? tops[^1] : -1; at >= 0; at = back[at])
            run.Add(pairs[at]);
        run.Reverse();
        return run;
    }

    /// <summary>
    /// Helper method for the lines that are the same in both, as pairs of where they are in each, in order. Myers'
    /// greedy forward search, with every step's furthest reaches kept to walk back through. Null for too many edits.
    /// </summary>
    private static List<(int Old, int New)>? Matches(IReadOnlyList<string> a, int aStart, int n, IReadOnlyList<string> b, int bStart, int m)
    {
        int max = n + m;
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;

        for (int d = 0; d <= Math.Min(max, MOST_EDITS) && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }

            // Where each diagonal got to after this many edits, -d to d
            var reached = new int[2 * d + 1];
            Array.Copy(v, offset - d, reached, 0, reached.Length);
            trace.Add(reached);
        }

        if (found < 0)
            return null;

        // Back from the end, every diagonal stretch is lines that are the same
        var same = new List<(int, int)>();
        int px = n, py = m;
        for (int d = found; d > 0; d--)
        {
            int[] previous = trace[d - 1];
            int k = px - py;
            int Reach(int diagonal) => previous[diagonal + d - 1];
            int prevK = k == -d || (k != d && Reach(k - 1) < Reach(k + 1)) ? k + 1 : k - 1;
            int prevX = Reach(prevK);
            int prevY = prevX - prevK;

            while (px > prevX && py > prevY)
            {
                px--;
                py--;
                same.Add((aStart + px, bStart + py));
            }

            px = prevX;
            py = prevY;
        }

        while (px > 0 && py > 0)
        {
            px--;
            py--;
            same.Add((aStart + px, bStart + py));
        }

        same.Reverse();
        return same;
    }
}
