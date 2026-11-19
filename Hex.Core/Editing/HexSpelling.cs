using System.Text.RegularExpressions;

namespace Hex.Core;

internal readonly record struct SpellingLine(int Number, string Text);
internal readonly record struct SpellingIssue(int Line, int Column, int Length, string Word, IReadOnlyList<string> Suggestions);
internal sealed record SpellingResult(string? Language, IReadOnlyList<SpellingIssue> Issues, int? Error = null);

/// <summary>Builds prose snapshots without changing the UTF-16 columns returned by the dictionary.</summary>
internal static partial class HexSpelling
{
    public const int MaxLineLength = 2048;

    // Keep links, paths, handles, acronyms and identifiers out of the native dictionary.
    [GeneratedRegex("""\]\([^\r\n)]*\)|(?<![\p{L}\p{N}])(?:\w+://|www\.)\S+|[\w.+-]+@[\w.-]+\.\w+|(?:[\w.-]*[/\\])+[\w./\\-]+|[\p{L}\p{N}_-]+(?:\.[\p{L}\p{N}_-]+)+|(?<![\p{L}\p{N}_])(?:@|/|\$)[\w:-]+|[\p{L}\p{N}_]+(?:_[\p{L}\p{N}_]+)|\b(?:[a-z]+[A-Z]\w*|[A-Z]{2,}[\p{L}\p{N}]*|[A-Z][a-z]+[A-Z]\w*|\w*\d\w*)\b""",
        RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalText();

    public static string Mask(string line, bool inlineCode)
    {
        char[]? masked = null;
        void Hide(int start, int length)
        {
            masked ??= line.ToCharArray();
            masked.AsSpan(start, length).Fill(' ');
        }
        if (inlineCode)
        {
            const char backtick = '\u0060';
            int start = 0;
            while ((start = line.IndexOf(backtick, start)) >= 0)
            {
                int end = start + 1;
                while (end < line.Length && line[end] == backtick) end++;
                int ticks = end - start;
                int next = end;
                bool found = false;
                while ((next = line.IndexOf(backtick, next)) >= 0)
                {
                    int after = next + 1;
                    while (after < line.Length && line[after] == backtick) after++;
                    if (after - next == ticks)
                    {
                        Hide(start, after - start);
                        start = after;
                        found = true;
                        break;
                    }
                    next = after;
                }
                if (!found) start = end;
            }
        }
        foreach (Match match in TechnicalText().Matches(masked is null ? line : new string(masked)))
            Hide(match.Index, match.Length);
        for (int i = 0; i < line.Length; i++)
            if (line[i] == '\0') Hide(i, 1);
        return masked is null ? line : new string(masked);
    }

    public static List<SpellingLine> MarkdownLines(string text, int first, int last, CancellationToken cancel)
    {
        var found = new List<SpellingLine>();
        using var lines = new StringReader(text);
        char fence = '\0';
        int fenceLength = 0;
        for (int number = 0; number <= last && lines.ReadLine() is { } line; number++)
        {
            if (cancel.IsCancellationRequested) return [];
            ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
            int ticks = 0;
            bool marker = line.Length - trimmed.Length <= 3 && !trimmed.IsEmpty && trimmed[0] is '\u0060' or '~';
            while (marker && ticks < trimmed.Length && trimmed[ticks] == trimmed[0]) ticks++;
            if (ticks >= 3)
            {
                if (fenceLength == 0)
                {
                    fence = trimmed[0];
                    fenceLength = ticks;
                    continue;
                }
                if (fence == trimmed[0] && ticks >= fenceLength && trimmed[ticks..].IsWhiteSpace())
                {
                    fenceLength = 0;
                    continue;
                }
            }
            if (fenceLength == 0 && number >= first && line.Length is > 0 and <= MaxLineLength
                && !line.StartsWith("    ", StringComparison.Ordinal) && !line.StartsWith('\t'))
                found.Add(new(number, Mask(line, inlineCode: true)));
        }
        return found;
    }
}
