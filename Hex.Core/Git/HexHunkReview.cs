namespace Hex.Core.Git;

/// <summary>
/// A file's changes since a checkpoint as hunks to look at one by one, and one of them put back as it was without touching
/// the rest. What Agentic's Changes offers in place of a whole file's restore, the way a review keeps some of an agent's
/// changes and undoes others.
/// </summary>
internal static class HexHunkReview
{
    /// <summary>The hunks between how a file was and how it is, with the lines each takes out and puts in.</summary>
    public static List<(GitHunk Hunk, string[] Old, string[] New)> Hunks(string before, string now)
    {
        string[] old = GitLineDiff.Lines(before), current = GitLineDiff.Lines(now);
        return [.. GitLineDiff.Diff(old, current).Select(hunk => (hunk, old[hunk.OldStart..(hunk.OldStart + hunk.OldCount)], current[hunk.NewStart..(hunk.NewStart + hunk.NewCount)]))];
    }

    /// <summary>
    /// The text with one hunk put back as it was, found again by its lines in the text as it is now, so a file changed
    /// since the hunks were worked out still has the right lines put back. Null when the hunk isn't there any more. The
    /// file's line ending and whether it ends in one are kept.
    /// </summary>
    public static string? Undo(string before, string now, string[] oldLines, string[] newLines)
    {
        foreach (var (hunk, old, current) in Hunks(before, now))
        {
            if (!old.SequenceEqual(oldLines) || !current.SequenceEqual(newLines))
                continue;

            string[] lines = GitLineDiff.Lines(now);
            string[] made = [.. lines[..hunk.NewStart], .. old, .. lines[(hunk.NewStart + hunk.NewCount)..]];
            string ending = now.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            bool endsInOne = now.EndsWith('\n');
            return string.Join(ending, made) + (endsInOne && made.Length > 0 ? ending : string.Empty);
        }

        return null;
    }
}
