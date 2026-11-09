using System.Text;

namespace Hex.Core;

/// <summary>
/// What the harness says back, written a line at a time up to so many characters and no further. Past that a line is
/// counted rather than written, and the end says how much was left out and how to get it, so an agent knows it didn't
/// see everything instead of thinking it did.
/// </summary>
internal sealed class HexBudget(int most)
{
    // Kept back for the line that says what was left out, so it always fits
    private const int ROOM_FOR_THE_END = 160;

    private readonly StringBuilder said = new();
    private int leftOut;

    /// <summary>The most characters it's going to write.</summary>
    public int Most { get; } = Math.Max(400, most);

    /// <summary>Whether it's full and lines are only being counted now.</summary>
    public bool Full { get; private set; }

    /// <summary>How many lines didn't fit.</summary>
    public int LeftOut => leftOut;

    /// <summary>A line written, or counted when there's no room for it. False once it's full.</summary>
    public bool Line(string line)
    {
        if (!Full && said.Length + line.Length + 1 <= Most - ROOM_FOR_THE_END)
        {
            said.Append(line).Append('\n');
            return true;
        }

        Full = true;
        leftOut++;
        return false;
    }

    /// <summary>The whole of it, with a line at the end about what didn't fit and what to do about it.</summary>
    public string Finish(string howToGetMore)
    {
        if (leftOut > 0)
            said.Append($"... {leftOut} more lines left out to stay under {Most} characters, {howToGetMore}.\n");
        return said.ToString();
    }

    /// <summary>A line cut down to so many characters around a place in it, with dots where it was cut.</summary>
    public static string Cut(string line, int at = 0, int width = 160)
    {
        string trimmed = line.Trim();
        if (trimmed.Length <= width)
            return trimmed;

        // Where the place is in the trimmed line, kept in sight with a bit before it
        int start = Math.Clamp(at - (line.Length - line.TrimStart().Length) - width / 3, 0, trimmed.Length - width);
        return (start > 0 ? "..." : string.Empty) + trimmed.Substring(start, width) + (start + width < trimmed.Length ? "..." : string.Empty);
    }
}

/// <summary>
/// An answer of the harness, its text and how many characters the plain way of finding the same out would have read
/// (grep's lines with their whole paths, a file read whole), which is what it saved.
/// </summary>
internal readonly record struct HexAnswer(string Text, long Plain, bool Failed = false)
{
    public static HexAnswer Wrong(string why) => new(why, 0, Failed: true);
}
