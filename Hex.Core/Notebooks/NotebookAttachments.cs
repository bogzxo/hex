using System.Text.RegularExpressions;

namespace Hex.Core.Notebooks;

/// <summary>Markdown attachment references outside code spans and fences.</summary>
internal static partial class NotebookAttachments
{
    internal sealed record Piece(string Text, string? Name = null);

    public static List<Piece> Split(string source)
    {
        var result = new List<Piece>();
        int start = 0, i = 0;
        while (i < source.Length)
        {
            if (source[i] is '`' or '~')
            {
                char marker = source[i];
                int length = 1;
                while (i + length < source.Length && source[i + length] == marker) length++;
                if (marker == '`' || length >= 3)
                {
                    int end = source.IndexOf(new string(marker, length), i + length, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + length;
                    continue;
                }
            }
            if (source[i] == '!' && (i == 0 || source[i - 1] != '\\') && Reference().Match(source, i) is { Success: true } match && match.Index == i)
            {
                if (i > start) result.Add(new Piece(source[start..i]));
                result.Add(new Piece(match.Value, Uri.UnescapeDataString(match.Groups[1].Value)));
                i = start = i + match.Length;
            }
            else i++;
        }
        if (start < source.Length) result.Add(new Piece(source[start..]));
        return result;
    }

    [GeneratedRegex(@"!\[[^\]\r\n]*\]\(attachment:([^\s)]+)\)")]
    private static partial Regex Reference();
}
