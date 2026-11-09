using System.Text.RegularExpressions;

namespace Hex.Dawn;

// The fix for a missing using put "using System.Text;" after the last using of the file, under
// Horizon.Rendering.PostProcessing, wherever the server felt like. Visual Studio puts it where it goes in order, the
// System ones first, so a using a fix puts in a run of them is moved to its place in it.
internal sealed partial class HexLanguages
{
    // A using of a namespace on a line of its own, global or static or not, and what it's of
    private static readonly Regex UsingLine = new(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?<name>[A-Za-z_][\w.]*)\s*;\s*$", RegexOptions.Compiled);

    /// <summary>
    /// A server's edits to a file with any that put a single using in amongst the file's usings moved to where it goes
    /// in order, System's first and the rest by name. Anything else is left as it was.
    /// </summary>
    internal static IReadOnlyList<LanguageServerEdit> SortUsings(string text, IReadOnlyList<LanguageServerEdit> edits)
    {
        string[] lines = text.Split('\n');
        List<LanguageServerEdit>? sorted = null;
        for (int i = 0; i < edits.Count; i++)
        {
            if (UsingPlace(lines, edits[i]) is { } moved)
                (sorted ??= [.. edits])[i] = moved;
        }

        return sorted ?? edits;
    }

    // Where an edit that puts one using in goes in order among the usings around it, null for one that isn't that
    private static LanguageServerEdit? UsingPlace(string[] lines, LanguageServerEdit edit)
    {
        if (edit.Line != edit.EndLine || edit.Character != edit.EndCharacter || edit.Line >= lines.Length)
            return null;

        string put = edit.Text.Trim('\r', '\n');
        if (put.Contains('\n') || UsingLine.Match(put) is not { Success: true } adding)
            return null;

        // The line it goes in front of, a using put at the end of a line goes in front of the next
        int at = edit.Character == 0 ? edit.Line : edit.Line + 1;
        if (edit.Character != 0 && edit.Character < lines[edit.Line].TrimEnd('\r').Length)
            return null;

        // The run of usings it's in or right after, blank lines between groups of them and all
        int first = Math.Min(at, lines.Length) - 1;
        while (first >= 0 && lines[first].Trim().Length == 0)
            first--;
        if (first < 0 || !UsingLine.IsMatch(lines[first]))
        {
            first = at;
            while (first < lines.Length && lines[first].Trim().Length == 0)
                first++;
            if (first >= lines.Length || !UsingLine.IsMatch(lines[first]))
                return null;
        }

        int start = first, end = first;
        while (start > 0 && (UsingLine.IsMatch(lines[start - 1]) || lines[start - 1].Trim().Length == 0 && start > 1 && UsingLine.IsMatch(lines[start - 2])))
            start--;
        while (end + 1 < lines.Length && (UsingLine.IsMatch(lines[end + 1]) || lines[end + 1].Trim().Length == 0 && end + 2 < lines.Length && UsingLine.IsMatch(lines[end + 2])))
            end++;

        // After the last using that goes before it, or in front of the first when none does. A file whose usings
        // aren't in order has it by the ones of its kind (System.Text under System.Numerics) rather than at the top
        string name = adding.Groups["name"].Value;
        int before = start;
        for (int line = start; line <= end; line++)
        {
            if (UsingLine.Match(lines[line]) is { Success: true } there && UsingOrder(there.Groups["name"].Value, name) < 0)
                before = line + 1;
        }

        // Groups with a blank line between them, it goes in the one whose first word is its own (Horizon.Audio in
        // with the Horizon ones under the System ones, not on the end of the System ones)
        int next = before;
        while (next <= end && lines[next].Trim().Length == 0)
            next++;
        if (next > before && next <= end && before > start && UsingLine.Match(lines[next]) is { Success: true } after
            && UsingLine.Match(lines[before - 1]) is { Success: true } previous
            && RootOf(after.Groups["name"].Value) == RootOf(name) && RootOf(previous.Groups["name"].Value) != RootOf(name))
            before = next;

        string newline = edit.Text.Contains("\r\n") || lines[start].EndsWith('\r') ? "\r\n" : "\n";
        string indent = lines[start][..(lines[start].Length - lines[start].TrimStart().Length)];
        return new LanguageServerEdit(before, 0, before, 0, indent + put.Trim() + newline);
    }

    // The first word of a namespace, Horizon of Horizon.Audio
    private static string RootOf(string name) => name.IndexOf('.') is var dot and > 0 ? name[..dot] : name;

    /// <summary>The order two usings' namespaces go in, Visual Studio's, System and what's in it first and then by name.</summary>
    internal static int UsingOrder(string a, string b)
    {
        bool systemA = a == "System" || a.StartsWith("System.", StringComparison.Ordinal);
        bool systemB = b == "System" || b.StartsWith("System.", StringComparison.Ordinal);
        if (systemA != systemB)
            return systemA ? -1 : 1;

        int order = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : string.CompareOrdinal(a, b);
    }
}
