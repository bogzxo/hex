using Hex.Core.Git;

namespace Hex.Core.Merge;

/// <summary>
/// A three-way merge of lines worked out in Hex, diff3 the way git merge-file does it. It is there for when git can't be
/// run, so that two machines that both changed a file still get one conflict for each place they disagree, instead of the
/// whole file as a single conflict because nothing was known about what each side started from.
/// </summary>
internal static class MergeThreeWay
{
    /// <summary>
    /// What mine and theirs each changed in the base, as regions. Stretches of the base nobody touched and changes only one
    /// side made come out clean, a change both sides made the same way too, and where the two disagree there is a conflict
    /// for that place alone.
    /// </summary>
    public static IReadOnlyList<MergeRegion> Merge(IReadOnlyList<string> basis, IReadOnlyList<string> mine, IReadOnlyList<string> theirs,
        string mineLabel = "Mine", string baseLabel = "Base", string theirsLabel = "Theirs")
    {
        List<GitHunk> ours = GitLineDiff.Diff(basis, mine), others = GitLineDiff.Diff(basis, theirs);
        var regions = new List<MergeRegion>();
        int at = 0, mineAt = 0, theirsAt = 0;
        while (mineAt < ours.Count || theirsAt < others.Count)
        {
            // The chunk starts at whichever side's next change comes first in the base
            int start = mineAt < ours.Count && (theirsAt >= others.Count || ours[mineAt].OldStart <= others[theirsAt].OldStart)
                ? ours[mineAt].OldStart : others[theirsAt].OldStart;
            int end = start, mineFrom = mineAt, theirsFrom = theirsAt;

            // Changes whose lines in the base overlap or touch are one chunk, as in git: two lines added at the same place
            // are a conflict, and so is a change that ends where another begins. The chunk grows until neither side has a
            // change starting inside it or right at its end.
            bool grew = true;
            while (grew)
            {
                grew = false;
                while (mineAt < ours.Count && ours[mineAt].OldStart <= end)
                {
                    end = Math.Max(end, ours[mineAt].OldStart + ours[mineAt].OldCount);
                    mineAt++;
                    grew = true;
                }

                while (theirsAt < others.Count && others[theirsAt].OldStart <= end)
                {
                    end = Math.Max(end, others[theirsAt].OldStart + others[theirsAt].OldCount);
                    theirsAt++;
                    grew = true;
                }
            }

            if (at < start)
                regions.Add(new MergeRegion.Clean(Slice(basis, at, start)));
            at = end;

            string[] original = Slice(basis, start, end);
            string[] local = Rebuild(basis, mine, ours, mineFrom, mineAt, start, end);
            string[] remote = Rebuild(basis, theirs, others, theirsFrom, theirsAt, start, end);
            if (local.AsSpan().SequenceEqual(remote))
                regions.Add(new MergeRegion.Clean(local, original));
            else if (local.AsSpan().SequenceEqual(original))
                regions.Add(new MergeRegion.Clean(remote, original));
            else if (remote.AsSpan().SequenceEqual(original))
                regions.Add(new MergeRegion.Clean(local, original));
            else
                AddConflict(regions, original, local, remote, mineLabel, baseLabel, theirsLabel);
        }

        if (at < basis.Count)
            regions.Add(new MergeRegion.Clean(Slice(basis, at, basis.Count)));
        return Joined(regions);
    }

    /// <summary>The regions as text, one line each, with the markers still in for any conflict nobody has resolved yet.</summary>
    public static string Marked(IReadOnlyList<MergeRegion> regions)
    {
        var lines = new List<string>();
        foreach (MergeRegion region in regions)
            lines.AddRange(region is MergeRegion.Clean clean ? clean.Lines : ((MergeRegion.Conflict)region).OutputLines());
        return string.Join("\n", lines);
    }

    // A conflict covers only what truly differs, as in git's zealous merge: the lines both sides start with, and those both
    // end with, are the same on either side and go out into clean regions around it. The base stays whole on the conflict.
    private static void AddConflict(List<MergeRegion> regions, string[] original, string[] local, string[] remote,
        string mineLabel, string baseLabel, string theirsLabel)
    {
        int head = 0, most = Math.Min(local.Length, remote.Length);
        while (head < most && local[head] == remote[head])
            head++;
        int tail = 0;
        while (tail < most - head && local[^(tail + 1)] == remote[^(tail + 1)])
            tail++;

        if (head > 0)
            regions.Add(new MergeRegion.Clean(local[..head]));
        string[] mineLines = local[head..(local.Length - tail)], theirsLines = remote[head..(remote.Length - tail)];
        regions.Add(new MergeRegion.Conflict(original, mineLines, theirsLines,
            [$"<<<<<<< {mineLabel}", .. mineLines, $"||||||| {baseLabel}", .. original, "=======", .. theirsLines, $">>>>>>> {theirsLabel}"]));
        if (tail > 0)
            regions.Add(new MergeRegion.Clean(local[(local.Length - tail)..]));
    }

    // One side's lines for the stretch of the base from start to end, its changes in that stretch put in
    private static string[] Rebuild(IReadOnlyList<string> basis, IReadOnlyList<string> side, List<GitHunk> hunks, int from, int to, int start, int end)
    {
        var lines = new List<string>();
        int at = start;
        for (int i = from; i < to; i++)
        {
            GitHunk hunk = hunks[i];
            lines.AddRange(Slice(basis, at, hunk.OldStart));
            lines.AddRange(Slice(side, hunk.NewStart, hunk.NewStart + hunk.NewCount));
            at = hunk.OldStart + hunk.OldCount;
        }

        lines.AddRange(Slice(basis, at, end));
        return [.. lines];
    }

    // Clean regions side by side with no base lines are one stretch of text, so they are made one, and none is left empty
    private static List<MergeRegion> Joined(List<MergeRegion> regions)
    {
        var result = new List<MergeRegion>();
        foreach (MergeRegion region in regions)
        {
            if (region is MergeRegion.Clean { Lines.Count: 0, BaseLines: null })
                continue;
            if (region is MergeRegion.Clean { BaseLines: null } clean && result.Count > 0 && result[^1] is MergeRegion.Clean { BaseLines: null } before)
                result[^1] = new MergeRegion.Clean([.. before.Lines, .. clean.Lines]);
            else
                result.Add(region);
        }

        if (result.Count == 0)
            result.Add(new MergeRegion.Clean([]));
        return result;
    }

    private static string[] Slice(IReadOnlyList<string> lines, int from, int to)
    {
        var slice = new string[Math.Max(0, to - from)];
        for (int i = 0; i < slice.Length; i++)
            slice[i] = lines[from + i];
        return slice;
    }
}
