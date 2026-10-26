namespace Hex.Core;

/// <summary>
/// A declaration of a tasks.hor found in its text and swapped for another, so Set Up F5 writing the tasks again keeps
/// the rest of the file as it was written. It wrote the whole file out of the tasks it read, and the automations, the
/// functions they call and every comment went with it.
/// </summary>
internal static class TasksFileText
{
    /// <summary>
    /// Where the top level let (or const) of a name is in a file's code, from the let to the end of its value and the
    /// semicolon after it, null when there's none. What's in comments and texts doesn't count.
    /// </summary>
    public static (int Start, int End)? Span(string code, string name)
    {
        int depth = 0;
        for (int i = 0; i < code.Length;)
        {
            if (Skip(code, ref i))
                continue;

            char c = code[i];
            if (c is '{' or '[' or '(')
                depth++;
            else if (c is '}' or ']' or ')')
                depth--;
            else if (depth == 0 && (WordAt(code, i, "let") || WordAt(code, i, "const")))
            {
                int after = i + (code[i] == 'l' ? 3 : 5);
                int at = after;
                while (at < code.Length && char.IsWhiteSpace(code[at]))
                    at++;
                if (at > after && WordAt(code, at, name))
                    return (i, EndOf(code, at + name.Length));
            }

            i++;
        }

        return null;
    }

    /// <summary>A file's code with the declaration of a name swapped for another, or the other added at the end when it has none.</summary>
    public static string Replace(string code, string name, string declaration)
    {
        declaration = declaration.Trim();
        if (Span(code, name) is not { } span)
            return code.TrimEnd() + (code.Trim().Length > 0 ? "\n\n" : string.Empty) + declaration + "\n";

        return code[..span.Start] + declaration + code[span.End..];
    }

    // The end of a value from where its name ends, past the brackets it opened and the semicolon after them, or the
    // end of its line for a value without any
    private static int EndOf(string code, int from)
    {
        int depth = 0;
        bool opened = false, equals = false, value = false;
        for (int i = from; i < code.Length;)
        {
            if (Skip(code, ref i))
            {
                value |= equals;
                continue;
            }

            char c = code[i];
            if (equals && !char.IsWhiteSpace(c))
                value = true;
            if (c == '=' && !equals)
            {
                equals = true;
                i++;
                continue;
            }

            if (c is '{' or '[' or '(')
            {
                depth++;
                opened = true;
            }
            else if (c is '}' or ']' or ')')
            {
                depth--;
                if (depth == 0 && opened)
                    return Semicolon(code, i + 1);
            }
            else if (depth == 0 && c == ';')
            {
                return i + 1;
            }
            else if (depth == 0 && c == '\n' && !opened && value)
            {
                return i;
            }

            i++;
        }

        return code.Length;
    }

    // Past a semicolon straight after a value, on its line
    private static int Semicolon(string code, int at)
    {
        int i = at;
        while (i < code.Length && code[i] is ' ' or '\t')
            i++;
        return i < code.Length && code[i] == ';' ? i + 1 : at;
    }

    // A comment or a text skipped whole, true when there was one
    private static bool Skip(string code, ref int i)
    {
        if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
        {
            int end = code.IndexOf('\n', i);
            i = end < 0 ? code.Length : end;
            return true;
        }

        if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
        {
            int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = end < 0 ? code.Length : end + 2;
            return true;
        }

        if (code[i] is '"' or '`')
        {
            char quote = code[i];
            int at = i + 1;
            while (at < code.Length && code[at] != quote)
                at += code[at] == '\\' ? 2 : 1;
            i = Math.Min(code.Length, at + 1);
            return true;
        }

        return false;
    }

    // Whether a word is at a place and on its own, not part of a longer name
    private static bool WordAt(string code, int at, string word) =>
        string.CompareOrdinal(code, at, word, 0, word.Length) == 0
        && (at == 0 || !IsNamePart(code[at - 1]))
        && (at + word.Length >= code.Length || !IsNamePart(code[at + word.Length]));

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
