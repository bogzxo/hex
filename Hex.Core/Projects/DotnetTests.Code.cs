namespace Hex.Core.Projects;

// The code of a C# file with everything that isn't code blanked out, so finding tests by reading lines can't be fooled.
// A test of the engine had a namespace line inside a raw string it parses, and discovery put its class in that
// namespace, a filter that ran nothing. Comments, strings of every kind (regular, verbatim, interpolated with their
// holes, raw) and character literals go to spaces, the lines and the columns stay where they were
internal static partial class DotnetTests
{
    /// <summary>The text with its comments, strings and character literals turned to spaces, new lines kept.</summary>
    public static string CodeOnly(string text)
    {
        char[] code = text.ToCharArray();
        int at = 0;
        while (at < text.Length)
        {
            char c = text[at];
            int end = -1;
            if (c == '/' && at + 1 < text.Length && text[at + 1] == '/')
                end = text.IndexOf('\n', at) is var line and >= 0 ? line : text.Length;
            else if (c == '/' && at + 1 < text.Length && text[at + 1] == '*')
                end = text.IndexOf("*/", at + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : text.Length;
            else if (c == '\'')
                end = CharEnd(text, at);
            else if (c is '"' or '$' or '@')
                end = StringEnd(text, at);

            if (end < 0)
            {
                at++;
                continue;
            }

            for (int blank = at; blank < end; blank++)
            {
                if (code[blank] is not ('\n' or '\r'))
                    code[blank] = ' ';
            }

            at = Math.Max(end, at + 1);
        }

        return new string(code);
    }

    // Where a character literal that starts at a quote ends, past its closing quote, never past its line
    private static int CharEnd(string text, int at)
    {
        int from = at + 1 < text.Length && text[at + 1] == '\\' ? at + 3 : at + 2;
        for (int i = Math.Min(from, text.Length); i < text.Length; i++)
        {
            if (text[i] == '\'')
                return i + 1;
            if (text[i] == '\n')
                return i;
        }

        return text.Length;
    }

    /// <summary>
    /// Where a string that starts at a place ends, past its closing quotes, -1 when what's there doesn't start one (a $
    /// or an @ that's code, @class say). An interpolated string's holes are code with strings of their own in them, so
    /// they're walked to find the real end.
    /// </summary>
    private static int StringEnd(string text, int at)
    {
        int p = at, dollars = 0;
        bool verbatim = false;
        while (p < text.Length && text[p] == '$')
        {
            dollars++;
            p++;
        }

        if (p < text.Length && text[p] == '@')
        {
            verbatim = true;
            p++;
            while (p < text.Length && text[p] == '$')
            {
                dollars++;
                p++;
            }
        }

        if (p >= text.Length || text[p] != '"')
            return -1;

        int quotes = 0;
        while (p + quotes < text.Length && text[p + quotes] == '"')
            quotes++;

        // A raw string ends at as many quotes as it started with, its holes (if it has any) don't get to say three quotes
        if (quotes >= 3)
        {
            int close = text.IndexOf(new string('"', quotes), p + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + quotes;
        }

        // Two quotes is an empty string, unless it's verbatim, where the second one's an escaped quote
        if (quotes == 2 && !verbatim)
            return p + 2;

        for (int i = p + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (verbatim && c == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                return i + 1;
            }

            if (!verbatim)
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    return i + 1;
                if (c == '\n')
                    return i;
            }

            if (dollars > 0 && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                i = HoleEnd(text, i) - 1;
            }
        }

        return text.Length;
    }

    // Where an interpolated string's hole that opens at a brace ends, past its closing brace
    private static int HoleEnd(string text, int at)
    {
        int depth = 0;
        for (int i = at; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return i + 1;
            else if (c == '\'')
                i = CharEnd(text, i) - 1;
            else if (c is '"' or '$' or '@' && StringEnd(text, i) is var end and > 0)
                i = end - 1;
            else if (c == '\n' && depth > 0 && i + 1 < text.Length && text[i + 1] == '\n')
                return i; // a hole that never closes, a line between paragraphs is far enough
        }

        return text.Length;
    }
}
