using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>A rename being made: the old name, the new one, the line it was last seen on (from 0), and whether a second occurrence has been changed too.</summary>
internal sealed record Rename(string Path, string OldName, string NewName, int Line, bool Confirmed, long Since);

/// <summary>
/// Notices a rename in the edits as they're closed (docs/next-edit-plan.md, 2.2 B): a hunk that swaps one identifier for
/// another with the rest of its line the same, the old name still written somewhere else in the file. It remembers the
/// rename until the old name is typed back, none of it remains, or five minutes pass, and finds the places left to change
/// (whole words only, never in a string or a comment). Safe to use from any thread.
/// </summary>
internal sealed partial class RenameTracker(TimeProvider? clock = null)
{
    /// <summary>How long a rename is followed after it was last seen.</summary>
    public static readonly TimeSpan Age = TimeSpan.FromMinutes(5);

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly List<Rename> renames = [];
    private int stamp;

    /// <summary>Goes up whenever the renames followed change, so a caller knows to look again.</summary>
    public int Stamp
    {
        get
        {
            lock (gate)
                return stamp;
        }
    }

    /// <summary>
    /// Whether renames are followed (edit_predictions_renames). Off, none is noted and none is found; switching it off lets
    /// go of those followed.
    /// </summary>
    public bool Enabled
    {
        get
        {
            lock (gate)
                return enabled;
        }
        set
        {
            lock (gate)
            {
                if (enabled == value)
                    return;
                enabled = value;
                renames.Clear();
                stamp++;
            }
        }
    }

    private bool enabled = true;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierShape { get; }

    /// <summary>Looks at what one text became: the renames in it noted, those it undid or left behind forgotten.</summary>
    public void Observe(string path, string before, string after) =>
        ObserveHunks(path, after, PredictionHistory.Diffed(before, after));

    internal void ObserveHunks(string path, string after, IReadOnlyList<PredictionHistory.Hunk> hunks) =>
        ObserveHunks(path, after, null, hunks);

    /// <summary>The same for a text given as its lines: it is joined only if a rename is followed or an identifier was swapped.</summary>
    internal void ObserveHunks(string path, string[] lines, IReadOnlyList<PredictionHistory.Hunk> hunks) =>
        ObserveHunks(path, null, lines, hunks);

    private void ObserveHunks(string path, string? joined, string[]? lines, IReadOnlyList<PredictionHistory.Hunk> hunks)
    {
        lock (gate)
        {
            if (!enabled)
                return;
            long now = time.GetTimestamp();
            bool hash = HashComments(path);
            string After() => joined ??= string.Join('\n', lines!);

            // A rename whose new name is no longer written anywhere has been typed back (or away)
            for (int i = renames.Count - 1; i >= 0; i--)
            {
                Rename rename = renames[i];
                if (rename.Path == path && (Expired(rename, now) || !Contains(After(), rename.NewName)))
                    Remove(i);
            }

            foreach (PredictionHistory.Hunk hunk in hunks)
            {
                foreach ((string oldName, string newName, int line) in Swaps(hunk))
                {
                    int reverse = renames.FindIndex(r => r.Path == path && r.OldName == newName && r.NewName == oldName);
                    if (reverse >= 0)
                    {
                        Remove(reverse);
                        continue;
                    }
                    if (Occurrences(After(), oldName, hash).Count == 0)
                        continue;
                    Note(path, oldName, newName, line, now);
                }
            }
        }
    }

    private void Note(string path, string oldName, string newName, int line, long now)
    {
        int at = renames.FindIndex(r => r.Path == path && r.OldName == oldName);
        if (at < 0)
        {
            renames.Add(new Rename(path, oldName, newName, line, false, now));
        }
        else
        {
            Rename known = renames[at];
            if (known.Line == line)
            {
                // Typed on in the same place: the new name grows
                renames[at] = known with { NewName = newName, Since = now };
            }
            else if (known.NewName == newName)
            {
                renames[at] = known with { Confirmed = true, Since = now };
            }
            else if (!known.NewName.StartsWith(newName, StringComparison.Ordinal))
            {
                // A different name somewhere else, a rename of its own
                renames[at] = new Rename(path, oldName, newName, line, false, now);
            }
            // else: the second occurrence being typed, not yet all of it
        }
        stamp++;
    }

    private void Remove(int at)
    {
        renames.RemoveAt(at);
        stamp++;
    }

    private bool Expired(Rename rename, long now) => time.GetElapsedTime(rename.Since, now) > Age;

    /// <summary>The renames followed in a file, the oldest first.</summary>
    public IReadOnlyList<Rename> Active(string path)
    {
        lock (gate)
        {
            if (!enabled)
                return [];
            long now = time.GetTimestamp();
            for (int i = renames.Count - 1; i >= 0; i--)
            {
                if (Expired(renames[i], now))
                    Remove(i);
            }
            return [.. renames.Where(rename => rename.Path == path)];
        }
    }

    /// <summary>Stops following a rename.</summary>
    public void Drop(Rename rename)
    {
        lock (gate)
        {
            int at = renames.IndexOf(rename);
            if (at >= 0)
                Remove(at);
        }
    }

    /// <summary>Stops following every rename in a file.</summary>
    public void Forget(string path)
    {
        lock (gate)
        {
            if (renames.RemoveAll(rename => rename.Path == path) > 0)
                stamp++;
        }
    }

    /// <summary>The newest rename in a file as the prompts say it, null when none is followed or none of it remains in the text.</summary>
    public PredictionRenameHint? Hint(string path, string text)
    {
        IReadOnlyList<Rename> active = Active(path);
        if (active.Count == 0)
            return null;
        Rename last = active[^1];
        int left = Remaining(text, last, path).Count;
        return left == 0 ? null : new PredictionRenameHint(last.OldName, last.NewName, left);
    }

    /// <summary>The places the old name is still written, as spans of the text, in order, outside strings and comments.</summary>
    public static IReadOnlyList<(int Start, int End)> Remaining(string text, Rename rename, string? path = null) =>
        Occurrences(text, rename.OldName, HashComments(path ?? rename.Path));

    /// <summary>Whether a file's comments start with a hash (Python and its kin) rather than slashes.</summary>
    public static bool HashComments(string path)
    {
        string extension = System.IO.Path.GetExtension(path.Split('#')[0]).ToLowerInvariant();
        return extension is ".py" or ".pyi" or ".rb" or ".sh" or ".yml" or ".yaml" or ".toml" or ".r" or ".pl" or ".ps1";
    }

    private static bool Contains(string text, string name) => Occurrences(text, name, false, anywhere: true).Count > 0;

    // The identifiers in a hunk swapped for others: the changed rows paired line by line, and for a pair the part that
    // differs, taken out to whole words, which has to be an identifier either side
    private static IEnumerable<(string Old, string New, int Line)> Swaps(PredictionHistory.Hunk hunk)
    {
        var minus = new List<string>();
        var plus = new List<(string Row, int Line)>();
        int line = hunk.NewStart;
        var found = new List<(string, string, int)>();

        void Flush()
        {
            if (minus.Count > 0 && minus.Count == plus.Count)
            {
                for (int i = 0; i < minus.Count; i++)
                {
                    if (Swap(minus[i], plus[i].Row) is { } swap)
                        found.Add((swap.Old, swap.New, plus[i].Line));
                }
            }
            minus.Clear();
            plus.Clear();
        }

        foreach (string row in hunk.Rows)
        {
            switch (row[0])
            {
                case '-':
                    if (plus.Count > 0)
                        Flush();
                    minus.Add(row[1..]);
                    break;
                case '+':
                    plus.Add((row[1..], line));
                    line++;
                    break;
                default:
                    Flush();
                    line++;
                    break;
            }
        }
        Flush();
        return found;
    }

    /// <summary>The identifier a line had that another has in its place, the rest of the line the same, or null.</summary>
    internal static (string Old, string New)? Swap(string old, string updated)
    {
        if (old == updated)
            return null;

        int prefix = 0, limit = Math.Min(old.Length, updated.Length);
        while (prefix < limit && old[prefix] == updated[prefix])
            prefix++;
        int suffix = 0;
        while (suffix < limit - prefix && old[old.Length - 1 - suffix] == updated[updated.Length - 1 - suffix])
            suffix++;

        // The edges of what changed moved out to the edges of the words they are in
        while (prefix > 0 && IsWord(old[prefix - 1])
            && ((prefix < old.Length - suffix && IsWord(old[prefix])) || (prefix < updated.Length - suffix && IsWord(updated[prefix]))))
            prefix--;
        while (suffix > 0 && IsWord(old[old.Length - suffix])
            && ((old.Length - suffix - 1 >= 0 && IsWord(old[old.Length - suffix - 1])) || (updated.Length - suffix - 1 >= 0 && IsWord(updated[updated.Length - suffix - 1]))))
            suffix--;

        string a = old[prefix..(old.Length - suffix)], b = updated[prefix..(updated.Length - suffix)];
        if (a == b || !IdentifierShape.IsMatch(a) || !IdentifierShape.IsMatch(b))
            return null;
        return (a, b);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Whole-word places of a name. Outside strings and comments unless anywhere is set (then only the words are looked at).
    private static List<(int Start, int End)> Occurrences(string text, string name, bool hash, bool anywhere = false)
    {
        var found = new List<(int, int)>();
        int n = text.Length, i = 0;
        while (i < n)
        {
            char c = text[i];
            if (!anywhere)
            {
                if (hash ? c == '#' : c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    i = EndOfLine(text, i);
                    continue;
                }
                if (!hash && c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? n : close + 2;
                    continue;
                }
                if (c is '"' or '\'' || (!hash && c == '`'))
                {
                    i = EndOfString(text, i, hash);
                    continue;
                }
            }

            if (IsWord(c))
            {
                int j = i + 1;
                while (j < n && IsWord(text[j]))
                    j++;
                if (j - i == name.Length && !char.IsDigit(c) && string.CompareOrdinal(text, i, name, 0, name.Length) == 0)
                    found.Add((i, j));
                i = j;
                continue;
            }
            i++;
        }
        return found;
    }

    private static int EndOfLine(string text, int at)
    {
        int end = text.IndexOf('\n', at);
        return end < 0 ? text.Length : end;
    }

    // Past the string that starts at a quote: triple quotes in Python run over lines, a backtick string too, a C# verbatim
    // string doubles its quotes, the rest end with their line
    private static int EndOfString(string text, int at, bool hash)
    {
        char quote = text[at];
        int n = text.Length;
        if (hash && at + 2 < n && text[at + 1] == quote && text[at + 2] == quote)
        {
            int close = text.IndexOf(new string(quote, 3), at + 3, StringComparison.Ordinal);
            return close < 0 ? n : close + 3;
        }

        bool verbatim = !hash && quote == '"' && at > 0 && text[at - 1] == '@';
        bool lines = quote == '`';
        int i = at + 1;
        while (i < n)
        {
            char c = text[i];
            if (c == '\n' && !lines)
                return i;
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    return i + 1;
                }
            }
            else if (c == '\\')
            {
                i += 2;
                continue;
            }
            else if (c == quote)
            {
                return i + 1;
            }
            i++;
        }
        return n;
    }
}
