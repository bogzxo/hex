using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>
/// A glob of VS Code's files.exclude and search.exclude, or a line of a .gitignore, matched against a path inside the
/// folder it's for written with forward slashes. ** is any number of folders, * anything but a slash, ? one character,
/// and a trailing slash only matches folders. A pattern without an internal or leading slash matches a name at
/// any depth. VS Code exclude patterns also support braces and match without regard to case.
/// </summary>
internal sealed partial class HexGlob
{
    // The pattern's parts between its slashes, matched part by part against the path's
    private readonly string[] parts;
    private readonly Regex?[]? ignoreParts;

    /// <summary>Whether it only matches folders, it was written with a slash at its end.</summary>
    public bool FolderOnly { get; }

    /// <summary>Whether it goes from the top of the folder rather than matching a name anywhere.</summary>
    public bool Anchored { get; }

    private HexGlob(string[] parts, bool folderOnly, bool anchored, Regex?[]? ignoreParts = null)
    {
        this.parts = parts;
        this.ignoreParts = ignoreParts;
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
        string[]? split = null;
        return Matches(relative, folder, ref split);
    }

    /// <summary>
    /// The same for a path asked of many globs, the path cut at its slashes the first time one that goes from the top
    /// needs it and kept in <paramref name="split"/> for the rest, where each of them cut it again.
    /// </summary>
    public bool Matches(string relative, bool folder, ref string[]? split)
    {
        if (FolderOnly && !folder)
            return false;

        if (!Anchored)
        {
            int slash = relative.LastIndexOf('/');
            return PartMatches(0, relative.AsSpan(slash + 1));
        }

        return Along(split ??= relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    // A globstar first matches no directories, then takes more when the next part does not match.
    private bool Along(string[] path)
    {
        int at = 0, on = 0, star = -1, consumed = 0;
        while (at < parts.Length || on < path.Length)
        {
            if (at < parts.Length && parts[at] == "**")
            {
                if (at == parts.Length - 1)
                    return ignoreParts is null || on < path.Length;
                star = at++;
                consumed = on;
            }
            else if (at < parts.Length && on < path.Length && PartMatches(at, path[on]))
            {
                at++;
                on++;
            }
            else if (star >= 0 && consumed < path.Length)
            {
                at = star + 1;
                on = ++consumed;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private bool PartMatches(int at, ReadOnlySpan<char> part) => ignoreParts is null
        ? Wild(parts[at], part) : ignoreParts[at]?.IsMatch(part) ?? true;

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
