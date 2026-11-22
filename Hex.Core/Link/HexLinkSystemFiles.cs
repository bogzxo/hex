namespace Hex.Core;

/// <summary>Names the operating system writes into a working tree, independent of Git and editor exclusions.</summary>
internal static class HexLinkSystemFiles
{
    public const int MaximumReported = 24;
    public const string Reason = "Excluded operating system metadata.";
    private static readonly string[] Names = [".DS_Store", ".Spotlight-V100", ".Trashes", ".fseventsd",
        "Thumbs.db", "ehthumbs.db", "desktop.ini", "$RECYCLE.BIN"];

    public static bool Leaf(ReadOnlySpan<char> leaf)
    {
        if (leaf.IsEmpty || leaf[0] is not ('.' or '$' or 'd' or 'D' or 'e' or 'E' or 't' or 'T'))
            return false;
        if (leaf.StartsWith("._", StringComparison.Ordinal))
            return true;
        foreach (string name in Names)
        {
            if (leaf.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool Matches(ReadOnlySpan<char> relative)
    {
        foreach (Range part in relative.Split('/'))
        {
            if (Leaf(relative[part]))
                return true;
        }

        return false;
    }
}
