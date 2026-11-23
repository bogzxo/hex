using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core;

internal sealed partial class HexGlob
{
    // Ignore files treat braces and leading spaces literally, and backslash escapes the following character.
    internal static HexGlob? ParseIgnore(string pattern, bool ignoreCase)
    {
        int end = pattern.Length;
        while (end > 0 && pattern[end - 1] == ' ')
        {
            int escape = end - 2;
            while (escape >= 0 && pattern[escape] == '\\')
                escape--;
            if ((end - 2 - escape) % 2 != 0)
                break;
            end--;
        }

        pattern = pattern[..end];
        bool folderOnly = pattern.EndsWith('/');
        bool leading = pattern.StartsWith('/');
        int start = leading ? 1 : 0;
        int length = pattern.Length - start - (folderOnly ? 1 : 0);
        if (length <= 0)
            return null;

        string[] parts = pattern.Substring(start, length).Split('/');
        if (parts.Any(part => part.Length == 0))
            return null;
        var matchers = new Regex?[parts.Length];
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline;
        if (ignoreCase)
            options |= RegexOptions.IgnoreCase;
        try
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "**")
                    continue;
                if (IgnoreExpression(parts[i]) is not { } expression)
                    return null;
                matchers[i] = new Regex(expression, options);
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return null;
        }
        return new HexGlob(parts, folderOnly, leading || parts.Length > 1, matchers);
    }

    private static string? IgnoreExpression(string pattern)
    {
        var expression = new StringBuilder(@"\A");
        for (int i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*':
                    expression.Append(".*");
                    while (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        i++;
                    break;
                case '?':
                    expression.Append('.');
                    break;
                case '[':
                    if (!AppendIgnoreClass(expression, pattern, ref i))
                        return null;
                    break;
                case '\\':
                    if (++i == pattern.Length)
                        return null;
                    goto default;
                default:
                    expression.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }
        return expression.Append(@"\z").ToString();
    }

    private static bool AppendIgnoreClass(StringBuilder expression, string pattern, ref int i)
    {
        expression.Append('[');
        int first = ++i;
        if (i < pattern.Length && pattern[i] is '!' or '^')
        {
            expression.Append('^');
            first = ++i;
        }
        for (; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == ']' && i > first)
            {
                expression.Append(']');
                return true;
            }
            if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':')
            {
                int close = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close < 0 || IgnoreCharacterClass(pattern[(i + 2)..close]) is not { } characters)
                    return false;
                expression.Append(characters);
                i = close + 1;
                continue;
            }
            bool literal = c == '\\';
            if (literal)
            {
                if (++i == pattern.Length)
                    return false;
                c = pattern[i];
            }
            if (c is '\\' or '[' or ']' or '^' || (literal && c == '-'))
                expression.Append('\\');
            expression.Append(c);
        }
        return false;
    }

    private static string? IgnoreCharacterClass(string kind) => kind switch
    {
        "alnum" => "A-Za-z0-9",
        "alpha" => "A-Za-z",
        "blank" => " \\t",
        "cntrl" => @"\x00-\x1f\x7f",
        "digit" => "0-9",
        "graph" => @"\x21-\x7e",
        "lower" => "a-z",
        "print" => @"\x20-\x7e",
        "punct" => @"\x21-\x2f\x3a-\x40\x5b-\x60\x7b-\x7e",
        "space" => @"\x09-\x0d ",
        "upper" => "A-Z",
        "xdigit" => "A-Fa-f0-9",
        _ => null
    };
}
