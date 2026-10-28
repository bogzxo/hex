using System.Text;

namespace Hex.Core.Notebooks;

/// <summary>Math boundaries in notebook Markdown, with code spans and fenced blocks left verbatim.</summary>
internal static class NotebookMath
{
    internal sealed record Piece(string Text, bool Math = false, bool Display = false);

    public static List<Piece> Split(string text)
    {
        var result = new List<Piece>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            if (text[i] is '`' or '~')
            {
                char marker = text[i];
                int length = 1;
                while (i + length < text.Length && text[i + length] == marker) length++;
                if (marker == '`' || length >= 3)
                {
                    string fence = new(marker, length);
                    int end = text.IndexOf(fence, i + length, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + length;
                    continue;
                }
            }
            bool escaped = i > 0 && text[i - 1] == '\\';
            string? open = !escaped && text.AsSpan(i).StartsWith("$$") ? "$$"
                : !escaped && text.AsSpan(i).StartsWith("\\[") ? "\\["
                : !escaped && text.AsSpan(i).StartsWith("\\(") ? "\\("
                : !escaped && text[i] == '$' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) ? "$" : null;
            if (open is null) { i++; continue; }
            string close = open switch { "\\[" => "\\]", "\\(" => "\\)", _ => open };
            int finish = i + open.Length;
            while ((finish = text.IndexOf(close, finish, StringComparison.Ordinal)) >= 0)
            {
                if (finish > 0 && text[finish - 1] != '\\' && (open != "$" || !char.IsWhiteSpace(text[finish - 1]) && (finish + 1 == text.Length || !char.IsDigit(text[finish + 1])))) break;
                finish += close.Length;
            }
            if (finish < 0) { i += open.Length; continue; }
            if (i > start) result.Add(new Piece(text[start..i]));
            result.Add(new Piece(text[(i + open.Length)..finish], true, open is "$$" or "\\["));
            i = start = finish + close.Length;
        }
        if (start < text.Length) result.Add(new Piece(text[start..]));
        return result;
    }

    /// <summary>Literal prose escaped for the text math renderer. Formula bodies are never escaped.</summary>
    public static string EscapeText(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text)
            result.Append(c switch { '\\' => "\\textbackslash{}", '{' => "\\{", '}' => "\\}", '$' => "\\$", '%' => "\\%", '#' => "\\#", '&' => "\\&", '_' => "\\_", '^' => "\\textasciicircum{}", '~' => "\\textasciitilde{}", _ => c.ToString() });
        return result.ToString();
    }

    public static string MarkdownText(string text)
    {
        var result = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            string? delimiter = text.AsSpan(i).StartsWith("**") ? "**" : text[i] == '`' ? "`" : text[i] == '*' ? "*" : null;
            int end = delimiter is not null ? text.IndexOf(delimiter, i + delimiter.Length, StringComparison.Ordinal) : -1;
            if (delimiter is not null && end > i + delimiter.Length)
            {
                string command = delimiter switch { "**" => "\\textbf{", "*" => "\\textit{", _ => "\\texttt{" };
                result.Append(command).Append(EscapeText(text[(i + delimiter.Length)..end])).Append('}');
                i = end + delimiter.Length;
            }
            else
            {
                int start = i++;
                while (i < text.Length && text[i] is not ('*' or '`')) i++;
                result.Append(EscapeText(text[start..i]));
            }
        }
        return result.ToString();
    }
}
