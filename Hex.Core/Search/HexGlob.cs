namespace Hex.Core;

/// <summary>
/// A glob of VS Code's files.exclude and search.exclude, or a line of a .gitignore, matched against a path inside the
/// folder it's for written with forward slashes. ** is any number of folders, * anything but a slash, ? one character,
/// {a,b} either, and a trailing slash only matches folders. One with no slash in it but at its end matches a name at
/// any depth (bin/ is every bin), one with a slash in it goes from the top of the folder (src/gen, or /out). Case is
/// ignored, the way Windows names files.
/// </summary>
internal sealed class HexGlob
{
    // The pattern's parts between its slashes, matched part by part against the path's
    private readonly string[] parts;

    /// <summary>Whether it only matches folders, it was written with a slash at its end.</summary>
    public bool FolderOnly { get; }

    /// <summary>Whether it goes from the top of the folder rather than matching a name anywhere.</summary>
    public bool Anchored { get; }

    private HexGlob(string[] parts, bool folderOnly, bool anchored)
    {
        this.parts = parts;
        FolderOnly = folderOnly;
        Anchored = anchored;
    }

    /// <summary>A glob out of what was written, every one of them for a pattern with braces, none for an empty one.</summary>
    public static IEnumerable<HexGlob> Parse(string pattern)
    {
        foreach (string each in Expanded(pattern.Trim().Replace('\\', '/')))
        {
            string written = each;
            bool folderOnly = written.EndsWith('/');
            written = written.TrimEnd('/');

            // ./src is src, a leading slash is the top of the folder
            if (written.StartsWith("./", StringComparison.Ordinal))
                written = written[2..];
            bool leading = written.StartsWith('/');
            written = written.TrimStart('/');
            if (written.Length == 0)
                continue;

            yield return new HexGlob(written.Split('/', StringSplitOptions.RemoveEmptyEntries), folderOnly, leading || written.Contains('/'));
        }
    }

    /// <summary>Every glob of some patterns, the ones that make no sense left out.</summary>
    public static HexGlob[] All(IEnumerable<string> patterns) => [.. patterns.SelectMany(Parse)];

    /// <summary>Whether a path inside the folder matches it, a folder's or a file's.</summary>
    public bool Matches(string relative, bool folder)
    {
        if (FolderOnly && !folder)
            return false;

        if (!Anchored)
        {
            int slash = relative.LastIndexOf('/');
            return Wild(parts[0], relative.AsSpan(slash + 1));
        }

        return Along(parts, 0, relative.Split('/', StringSplitOptions.RemoveEmptyEntries), 0);
    }

    // The pattern's parts from one place against the path's from another, ** taking none of them or as many as it likes
    private static bool Along(string[] pattern, int at, string[] path, int on)
    {
        while (at < pattern.Length)
        {
            if (pattern[at] == "**")
            {
                // A ** at the end is everything under where it got to
                if (at == pattern.Length - 1)
                    return true;

                for (int skip = on; skip <= path.Length; skip++)
                {
                    if (Along(pattern, at + 1, path, skip))
                        return true;
                }

                return false;
            }

            if (on >= path.Length || !Wild(pattern[at], path[on]))
                return false;
            at++;
            on++;
        }

        return on == path.Length;
    }

    // One part against a name, * any run of it and ? any one character
    private static bool Wild(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        int p = 0, n = 0, star = -1, resume = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    // The first {a,b} of a pattern made into one pattern each, and theirs in turn
    private static IEnumerable<string> Expanded(string pattern)
    {
        int open = pattern.IndexOf('{');
        int close = open < 0 ? -1 : pattern.IndexOf('}', open);
        if (close < 0)
        {
            yield return pattern;
            yield break;
        }

        foreach (string choice in pattern[(open + 1)..close].Split(','))
        {
            foreach (string each in Expanded(pattern[..open] + choice + pattern[(close + 1)..]))
                yield return each;
        }
    }
}
