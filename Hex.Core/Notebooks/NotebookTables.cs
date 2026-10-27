using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core.Notebooks;

/// <summary>A table out of markdown or HTML, its header, its rows, and how each column lines up ('l', 'c' or 'r').</summary>
internal sealed record NotebookTable(List<string> Header, List<List<string>> Rows, List<char> Align);

/// <summary>
/// Tables in what a notebook shows. Markdown's (GitHub's pipes, a header row, a row of dashes and colons saying how each
/// column lines up, then the rows) split out of the text round them, and the HTML table a pandas DataFrame shows itself
/// as read into rows, so both are drawn as tables rather than as their markup. What's in a cell is its text, the markup
/// of markdown or the tags of HTML in it are taken off.
/// </summary>
internal static partial class NotebookTables
{
    /// <summary>Markdown in pieces, text and tables in the order they come, a fence of code never cut into.</summary>
    public static List<(string? Text, NotebookTable? Table)> Split(string markdown)
    {
        var pieces = new List<(string? Text, NotebookTable? Table)>();
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = new StringBuilder();
        bool fenced = false;

        void Flush()
        {
            if (text.Length > 0)
                pieces.Add((text.ToString().TrimEnd('\n'), null));
            text.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;

            // A row of cells with a row of dashes under it starts a table
            if (!fenced && i + 1 < lines.Length && IsRow(line) && Alignment(lines[i + 1]) is { } align && Cells(line).Count == align.Count)
            {
                Flush();
                var header = Cells(line);
                var rows = new List<List<string>>();
                i += 2;
                while (i < lines.Length && IsRow(lines[i]))
                {
                    var cells = Cells(lines[i]);
                    while (cells.Count < header.Count) cells.Add(string.Empty);
                    rows.Add(cells.GetRange(0, header.Count));
                    i++;
                }

                i--;
                pieces.Add((null, new NotebookTable(header.Select(Plain).ToList(), rows.Select(row => row.Select(Plain).ToList()).ToList(), align)));
                continue;
            }

            text.Append(line).Append('\n');
        }

        Flush();
        return pieces;
    }

    /// <summary>Whether there's a table anywhere in some markdown, to leave it a single label when there isn't.</summary>
    public static bool HasTable(string markdown) => markdown.Contains('|') && Split(markdown).Any(piece => piece.Table is not null);

    private static bool IsRow(string line) => line.Contains('|') && line.Trim().Length > 1;

    // The cells of a row, the pipes at its ends optional, a \| inside a cell not one between them
    private static List<string> Cells(string line)
    {
        string row = line.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal)) row = row[..^1];

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] == '\\' && i + 1 < row.Length && row[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (row[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(row[i]);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    // The row of dashes under a table's header, how each column lines up, null for a row that isn't one
    private static List<char>? Alignment(string line)
    {
        if (!line.Contains('-') || !IsRow(line) && !line.TrimStart().StartsWith('-'))
            return null;

        var align = new List<char>();
        foreach (string cell in Cells(line))
        {
            string dashes = cell.Trim();
            if (dashes.Length == 0 || dashes.Trim(':', '-').Length > 0 || !dashes.Contains('-'))
                return null;
            align.Add(dashes.StartsWith(':') && dashes.EndsWith(':') ? 'c' : dashes.EndsWith(':') ? 'r' : 'l');
        }

        return align;
    }

    // A cell's text without the markdown it was written in, bold, italic, code and links
    private static string Plain(string cell)
    {
        string text = LinkPattern().Replace(cell, "$1");
        return text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).Trim('*', '_', ' ');
    }

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    /// <summary>
    /// The first table of some HTML (a pandas DataFrame's), with what's said after it (pandas' "5 rows × 4 columns"),
    /// null when there's no table in it. The last row of the header is its header, the index's name or nothing over the
    /// column of the index, and the cells are their text with the tags taken off.
    /// </summary>
    public static NotebookTable? FromHtml(string html, out string? caption)
    {
        caption = null;
        int start = html.IndexOf("<table", StringComparison.OrdinalIgnoreCase);
        int end = start < 0 ? -1 : html.IndexOf("</table>", start, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0)
            return null;

        string table = html[start..end];
        int bodyAt = table.IndexOf("<tbody", StringComparison.OrdinalIgnoreCase);
        string head = bodyAt >= 0 ? table[..bodyAt] : string.Empty;
        string body = bodyAt >= 0 ? table[bodyAt..] : table;

        var headRows = Rows(head);
        var rows = Rows(body);
        if (headRows.Count == 0 && rows.Count > 0 && table.IndexOf("<th", StringComparison.OrdinalIgnoreCase) is var th and >= 0 && th < table.IndexOf("<td", StringComparison.OrdinalIgnoreCase))
        {
            headRows.Add(rows[0]);
            rows.RemoveAt(0);
        }

        int columns = Math.Max(headRows.Count > 0 ? headRows[^1].Count : 0, rows.Count > 0 ? rows.Max(row => row.Count) : 0);
        if (columns == 0)
            return null;

        // Each column's header is the last thing its header rows say, pandas puts the columns' names on one and the
        // index's name on a row of its own under it
        List<string> header = [.. Enumerable.Range(0, columns).Select(column =>
            headRows.Select(row => column < row.Count ? row[column] : string.Empty).LastOrDefault(said => said.Length > 0) ?? string.Empty)];
        foreach (var row in rows)
            while (row.Count < columns) row.Add(string.Empty);

        string after = html[(end + "</table>".Length)..];
        string said = Text(after).Trim();
        if (said.Length > 0)
            caption = said;

        var align = Enumerable.Range(0, columns).Select(column => column > 0 && rows.Count > 0 && rows.All(row => row[column].Length == 0 || double.TryParse(row[column], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) ? 'r' : 'l').ToList();
        return new NotebookTable(header, rows, align);
    }

    // The rows of some table HTML, each its cells' text, th and td alike
    private static List<List<string>> Rows(string html)
    {
        var rows = new List<List<string>>();
        foreach (Match row in RowPattern().Matches(html))
        {
            var cells = new List<string>();
            foreach (Match cell in CellPattern().Matches(row.Groups[1].Value))
            {
                cells.Add(Text(cell.Groups[2].Value).Trim());

                // A cell over several columns takes them all, empty after the first
                if (SpanPattern().Match(cell.Groups[1].Value) is { Success: true } span && int.TryParse(span.Groups[1].Value, out int spanned))
                    cells.AddRange(Enumerable.Repeat(string.Empty, Math.Clamp(spanned, 1, 64) - 1));
            }

            rows.Add(cells);
        }

        return rows;
    }

    // HTML as its text, tags off and entities written out
    private static string Text(string html) => WebUtility.HtmlDecode(TagPattern().Replace(html, string.Empty)).Replace(' ', ' ');

    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"<t[hd]([^>]*)>(.*?)</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"colspan\s*=\s*""?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpanPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();
}
