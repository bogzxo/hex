namespace Hex.Core.Git;

/// <summary>
/// A block a merge left in a file for somebody to decide, by the lines its markers are on. Current (ours, HEAD) is between
/// the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> and the <c>=======</c>, incoming (theirs) between that and the
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>, and with diff3 the common ancestor's after a <c>|||||||</c> in the first half.
/// </summary>
/// <param name="Base">The line of the <c>|||||||</c>, -1 when there's none.</param>
internal readonly record struct GitConflict(int Start, int Base, int Middle, int End)
{
    /// <summary>The last line of what's current, before the ancestor's or the middle.</summary>
    public int CurrentEnd => Base >= 0 ? Base : Middle;
}

/// <summary>Which side of a conflict is kept.</summary>
internal enum GitConflictPick { Current, Incoming, Both }

/// <summary>The conflicts a merge left in a text, found and settled.</summary>
internal static class GitConflicts
{
    private const string START = "<<<<<<<", BASE = "|||||||", MIDDLE = "=======", END = ">>>>>>>";

    /// <summary>Every whole conflict in some lines, in order. Markers that don't make a whole one (a stray =======) are left be.</summary>
    public static List<GitConflict> Find(IReadOnlyList<string> lines)
    {
        var found = new List<GitConflict>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(START, StringComparison.Ordinal))
                continue;

            int baseLine = -1, middle = -1, end = -1;
            for (int j = i + 1; j < lines.Count; j++)
            {
                string line = lines[j];
                if (line.StartsWith(START, StringComparison.Ordinal))
                    break;
                if (middle < 0 && baseLine < 0 && line.StartsWith(BASE, StringComparison.Ordinal))
                    baseLine = j;
                else if (middle < 0 && line.StartsWith(MIDDLE, StringComparison.Ordinal) && line.TrimEnd().Length == MIDDLE.Length)
                    middle = j;
                else if (middle >= 0 && line.StartsWith(END, StringComparison.Ordinal))
                {
                    end = j;
                    break;
                }
            }

            if (middle < 0 || end < 0)
                continue;

            found.Add(new GitConflict(i, baseLine, middle, end));
            i = end;
        }

        return found;
    }

    /// <summary>The lines with a conflict settled, the side picked (or both, current first) in place of the whole block.</summary>
    public static List<string> Settle(IReadOnlyList<string> lines, GitConflict conflict, GitConflictPick pick)
    {
        var settled = new List<string>(lines.Count);
        for (int i = 0; i < conflict.Start; i++)
            settled.Add(lines[i]);

        if (pick is GitConflictPick.Current or GitConflictPick.Both)
            for (int i = conflict.Start + 1; i < conflict.CurrentEnd; i++)
                settled.Add(lines[i]);
        if (pick is GitConflictPick.Incoming or GitConflictPick.Both)
            for (int i = conflict.Middle + 1; i < conflict.End; i++)
                settled.Add(lines[i]);

        for (int i = conflict.End + 1; i < lines.Count; i++)
            settled.Add(lines[i]);
        return settled;
    }
}
