using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core.Notebooks;

/// <summary>A table out of markdown or HTML, its header, its rows, and how each column lines up ('l', 'c' or 'r').</summary>
internal sealed record NotebookTable(List<string> Header, List<List<string>> Rows, List<char> Align,
    List<string>? MarkdownHeader = null, List<List<string>>? MarkdownRows = null);

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
        char fenceMarker = '\0';
        int fenceLength = 0;

        void Flush()
        {
            if (text.Length > 0)
                pieces.Add((text.ToString().TrimEnd('\n'), null));
            text.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.Length >= 3 && trimmed[0] is '`' or '~')
            {
                int length = 1;
                while (length < trimmed.Length && trimmed[length] == trimmed[0]) length++;
                if (!fenced && length >= 3) { fenced = true; fenceMarker = trimmed[0]; fenceLength = length; }
                else if (fenced && trimmed[0] == fenceMarker && length >= fenceLength) fenced = false;
            }

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
                pieces.Add((null, new NotebookTable(header.Select(Plain).ToList(), rows.Select(row => row.Select(Plain).ToList()).ToList(), align, header, rows)));
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
        int codeTicks = 0;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] == '`')
            {
                int length = 1;
                while (i + length < row.Length && row[i + length] == '`') length++;
                codeTicks = codeTicks == 0 ? length : codeTicks == length ? 0 : codeTicks;
                cell.Append('`', length);
                i += length - 1;
            }
            else if (row[i] == '\\' && i + 1 < row.Length && row[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (row[i] == '|' && codeTicks == 0)
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

        var headRows = Rows(head, header: true);
        var rows = Rows(body, header: false);
        if (headRows.Count == 0 && rows.Count > 0 && table.IndexOf("<th", StringComparison.OrdinalIgnoreCase) is var th and >= 0 && th < table.IndexOf("<td", StringComparison.OrdinalIgnoreCase))
        {
            headRows.Add(rows[0]);
            rows.RemoveAt(0);
        }

        int columns = Math.Max(headRows.Count > 0 ? headRows[^1].Count : 0, rows.Count > 0 ? rows.Max(row => row.Count) : 0);
        if (columns == 0)
            return null;

        // Each column's header is what its header rows say, one after the other. pandas puts the levels of a MultiIndex's
        // columns on a row each ("X" over "p" is "X p") and the index's names on a row of their own under them
        List<string> header = [.. Enumerable.Range(0, columns).Select(column => Joined(headRows.Select(row => column < row.Count ? row[column] : string.Empty)))];
        foreach (var row in rows)
            while (row.Count < columns) row.Add(string.Empty);

        // What's said about it, a caption of its own and what comes after it (pandas' "5 rows × 4 columns")
        string said = Text(html[(end + "</table>".Length)..]).Trim();
        if (CaptionPattern().Match(table) is { Success: true } named && Text(named.Groups[1].Value).Trim() is { Length: > 0 } title)
            said = said.Length > 0 ? $"{title}, {said}" : title;
        if (said.Length > 0)
            caption = said;

        var align = Enumerable.Range(0, columns).Select(column => column > 0 && rows.Count > 0 && rows.All(row => row[column].Length == 0 || double.TryParse(row[column], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) ? 'r' : 'l').ToList();
        return new NotebookTable(header, rows, align);
    }

    // The rows of some table HTML laid out on a grid, each its cells' text, th and td alike. A cell over several rows
    // (pandas' index label, said once for the rows it's over) is in each of them, the rows under it were a cell short and
    // their numbers sat under the wrong columns. A cell over several columns is in each of them in a header (pandas' level
    // over the columns of a MultiIndex, joined with the level under it into one name) and in the first of them elsewhere
    private static List<List<string>> Rows(string html, bool header)
    {
        var rows = new List<List<string>>();
        var down = new Dictionary<int, (int Left, string Text)>();
        foreach (Match row in RowPattern().Matches(html))
        {
            var cells = new List<string>();
            void Carried()
            {
                while (down.TryGetValue(cells.Count, out var over))
                {
                    cells.Add(over.Text);
                    if (over.Left <= 1)
                        down.Remove(cells.Count - 1);
                    else
                        down[cells.Count - 1] = (over.Left - 1, over.Text);
                }
            }

            foreach (Match cell in CellPattern().Matches(row.Groups[1].Value))
            {
                Carried();
                string text = Text(cell.Groups[2].Value).Trim();
                string said = cell.Groups[1].Value;
                int across = Spanned(ColumnSpanPattern().Match(said)), tall = Spanned(RowSpanPattern().Match(said));
                for (int i = 0; i < across; i++)
                {
                    if (tall > 1)
                        down[cells.Count] = (tall - 1, header || i == 0 ? text : string.Empty);
                    cells.Add(header || i == 0 ? text : string.Empty);
                }
            }

            Carried();
            rows.Add(cells);
        }

        return rows;
    }

    // Helper method for the levels of a column's header as its name, each once, a cell over two header rows said once
    private static string Joined(IEnumerable<string> levels)
    {
        var name = new StringBuilder();
        string last = string.Empty;
        foreach (string level in levels)
        {
            if (level.Length == 0 || level == last)
                continue;
            if (name.Length > 0)
                name.Append(' ');
            name.Append(level);
            last = level;
        }

        return name.ToString();
    }

    private static int Spanned(Match span) => span.Success && int.TryParse(span.Groups[1].Value, out int spanned) ? Math.Clamp(spanned, 1, 64) : 1;

    // HTML as its text, tags off and entities written out
    private static string Text(string html) => WebUtility.HtmlDecode(TagPattern().Replace(html, string.Empty)).Replace(' ', ' ');

    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"<t[hd]([^>]*)>(.*?)</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"colspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnSpanPattern();

    [GeneratedRegex(@"rowspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RowSpanPattern();

    [GeneratedRegex(@"<caption[^>]*>(.*?)</caption>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CaptionPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();
}
