using System.Text;

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
    /// since the hunks were worked out still has the right lines put back. Of two hunks with the same lines the one nearest
    /// where it was seen (<paramref name="near"/>) is taken, the first of them undid the wrong one. Null when the hunk isn't
    /// there any more. The file's line ending and whether it ends in one are kept.
    /// </summary>
    public static string? Undo(string before, string now, string[] oldLines, string[] newLines, int near = 0)
    {
        GitHunk? best = null;
        string[]? bestOld = null;
        foreach (var (hunk, old, current) in Hunks(before, now))
        {
            if (!old.SequenceEqual(oldLines) || !current.SequenceEqual(newLines))
                continue;
            if (best is null || Math.Abs(hunk.NewStart - near) < Math.Abs(best.Value.NewStart - near))
                (best, bestOld) = (hunk, old);
        }

        if (best is not { } found || bestOld is null)
            return null;

        string[] lines = GitLineDiff.Lines(now);
        string[] made = [.. lines[..found.NewStart], .. bestOld, .. lines[(found.NewStart + found.NewCount)..]];
        string ending = now.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsInOne = now.EndsWith('\n');
        return string.Join(ending, made) + (endsInOne && made.Length > 0 ? ending : string.Empty);
    }

    /// <summary>
    /// A file's text and the encoding it was written in, by its byte order mark, UTF-8 without one. Written back with
    /// <see cref="Write"/> it keeps both, a file of UTF-16 or with a mark came back as plain UTF-8 before.
    /// </summary>
    public static (string Text, Encoding Encoding) Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    /// <summary>A file written in the encoding it was read in, its byte order mark too when it had one.</summary>
    public static void Write(string path, string text, Encoding encoding) => File.WriteAllText(path, text, encoding);
}
