using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Core.Notebooks;

// Markdown written as HTML for an export. The blocks of it (headings, paragraphs, lists, quotes, code, rules, tables
// through NotebookTables and HTML of its own) and in them code spans, links, pictures, emphasis and formulas through
// NotebookMath, the parts notebooks use. What it makes goes through Clean like any other HTML, so markup a cell has in
// it is kept only as far as it's safe
internal static partial class NotebookHtmlExport
{
    // A placeholder for a piece of inline HTML already made, kept out of the way of emphasis
    private const char OPAQUE = '\u0001', OPAQUE_END = '\u0002';

    /// <summary>Markdown as safe HTML, pictures it attaches (attachment:name) taken out of the cell's attachments.</summary>
    internal static string Markdown(string source, JsonObject? attachments, NotebookHtmlOptions options)
    {
        var html = new StringBuilder();
        foreach (var (text, table) in NotebookTables.Split(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')))
        {
            if (table is not null)
                Table(html, table, attachments, options);
            else if (text is not null)
                Blocks(html, text.Split('\n'), attachments, options, tight: false);
        }

        return Clean(html.ToString()).Html + "\n";
    }

    private static void Table(StringBuilder html, NotebookTable table, JsonObject? attachments, NotebookHtmlOptions options)
    {
        List<string> header = table.MarkdownHeader ?? table.Header;
        List<List<string>> rows = table.MarkdownRows ?? table.Rows;
        string Align(int column) => column < table.Align.Count ? table.Align[column] switch { 'c' => " style=\"text-align:center\"", 'r' => " style=\"text-align:right\"", _ => string.Empty } : string.Empty;

        html.Append("<table class=\"markdown-table\"><thead><tr>");
        for (int column = 0; column < header.Count; column++)
            html.Append("<th").Append(Align(column)).Append('>').Append(Inline(header[column].Trim(), attachments, options)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        foreach (List<string> row in rows)
        {
            html.Append("<tr>");
            for (int column = 0; column < row.Count; column++)
                html.Append("<td").Append(Align(column)).Append('>').Append(Inline(row[column].Trim(), attachments, options)).Append("</td>");
            html.Append("</tr>");
        }

        html.Append("</tbody></table>\n");
    }

    // Helper method for lines of markdown as blocks. Tight is a list item's, its paragraphs not wrapped in <p>
    private static void Blocks(StringBuilder html, string[] lines, JsonObject? attachments, NotebookHtmlOptions options, bool tight)
    {
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            int indent = Indent(line);

            // Code fenced with ``` or ~~~, its first word the language
            if (indent < 4 && Fence(trimmed) is { } fence)
            {
                string language = trimmed[fence.Length..].Trim().Split(' ', 2)[0];
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !(lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal) && lines[i].Trim().Trim(fence[0]).Length == 0))
                {
                    code.Append(Unindent(lines[i], indent)).Append('\n');
                    i++;
                }

                i++;
                html.Append("<pre class=\"source\"><code");
                if (language.Length > 0)
                    html.Append(" class=\"language-").Append(Plain(language)).Append('"');
                html.Append('>').Append(Encode(code.ToString().TrimEnd('\n'))).Append("</code></pre>\n");
                continue;
            }

            // A formula on lines of its own between $$ or \[ and \]
            if (indent < 4 && (trimmed.StartsWith("$$", StringComparison.Ordinal) || trimmed.StartsWith("\\[", StringComparison.Ordinal)))
            {
                string close = trimmed.StartsWith("$$", StringComparison.Ordinal) ? "$$" : "\\]";
                var formula = new StringBuilder(trimmed[2..]);
                int end = i;
                bool closed = trimmed.Length > 2 && trimmed[2..].Contains(close, StringComparison.Ordinal);
                while (!closed && end + 1 < lines.Length)
                {
                    end++;
                    formula.Append('\n').Append(lines[end]);
                    closed = lines[end].Contains(close, StringComparison.Ordinal);
                }

                string said = formula.ToString();
                int at = said.IndexOf(close, StringComparison.Ordinal);
                if (closed && at >= 0 && said[(at + close.Length)..].Trim().Length == 0)
                {
                    html.Append(Formula(said[..at], display: true, options)).Append('\n');
                    i = end + 1;
                    continue;
                }
            }

            if (indent < 4 && Heading().Match(line) is { Success: true } heading)
            {
                int level = heading.Groups[1].Length;
                string text = heading.Groups[2].Value.Trim();
                html.Append("<h").Append(level).Append(" id=\"").Append(Encode(Slug(text))).Append("\">").Append(Inline(text, attachments, options)).Append("</h").Append(level).Append(">\n");
                i++;
                continue;
            }

            if (indent < 4 && Rule().IsMatch(line))
            {
                html.Append("<hr>\n");
                i++;
                continue;
            }

            if (indent < 4 && trimmed.StartsWith('>'))
            {
                var quoted = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0)
                {
                    string quote = lines[i].TrimStart();
                    if (quote.StartsWith('>'))
                        quote = quote.Length > 1 && quote[1] == ' ' ? quote[2..] : quote[1..];
                    quoted.Add(quote);
                    i++;
                }

                html.Append("<blockquote>\n");
                Blocks(html, [.. quoted], attachments, options, tight: false);
                html.Append("</blockquote>\n");
                continue;
            }

            if (indent < 4 && ListItem().Match(line) is { Success: true })
            {
                i = List(html, lines, i, attachments, options);
                continue;
            }

            if (indent >= 4 && !tight)
            {
                var code = new StringBuilder();
                while (i < lines.Length && (Indent(lines[i]) >= 4 || lines[i].Trim().Length == 0))
                {
                    code.Append(Unindent(lines[i], 4)).Append('\n');
                    i++;
                }

                html.Append("<pre class=\"source\"><code>").Append(Encode(code.ToString().TrimEnd('\n'))).Append("</code></pre>\n");
                continue;
            }

            // HTML of its own, a block of it up to the next blank line, made safe with the rest
            if (indent < 4 && HtmlBlock().IsMatch(trimmed))
            {
                while (i < lines.Length && lines[i].Trim().Length > 0)
                {
                    html.Append(lines[i]).Append('\n');
                    i++;
                }

                continue;
            }

            // A paragraph, up to a blank line or the start of another block, or a heading underlined with = or -
            var paragraph = new StringBuilder(trimmed);
            i++;
            int setext = 0;
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                string next = lines[i];
                string nextTrimmed = next.TrimStart();
                if (Indent(next) < 4 && Underline().Match(next) is { Success: true } under)
                {
                    setext = under.Groups[1].Value[0] == '=' ? 1 : 2;
                    i++;
                    break;
                }

                if (Indent(next) < 4 && (Fence(nextTrimmed) is not null || Heading().IsMatch(next) || Rule().IsMatch(next) || nextTrimmed.StartsWith('>')
                    || BulletItem().IsMatch(next) || HtmlBlock().IsMatch(nextTrimmed) || nextTrimmed.StartsWith("$$", StringComparison.Ordinal)))
                    break;
                paragraph.Append('\n').Append(next.TrimStart());
                i++;
            }

            string content = Inline(paragraph.ToString(), attachments, options);
            if (setext > 0)
                html.Append("<h").Append(setext).Append(" id=\"").Append(Encode(Slug(paragraph.ToString()))).Append("\">").Append(content).Append("</h").Append(setext).Append(">\n");
            else if (tight)
                html.Append(content).Append('\n');
            else
                html.Append("<p>").Append(content).Append("</p>\n");
        }
    }

    // Helper method for a list from its first item, returning the line after it. Items are what's indented under their
    // marker, a list is loose (paragraphs in <p>) when a blank line is between its items
    private static int List(StringBuilder html, string[] lines, int i, JsonObject? attachments, NotebookHtmlOptions options)
    {
        Match first = ListItem().Match(lines[i]);
        bool ordered = char.IsAsciiDigit(first.Groups[2].Value[0]);
        char delimiter = first.Groups[2].Value[^1];
        int markerIndent = first.Groups[1].Length;
        var items = new List<List<string>>();
        bool loose = false;

        while (i < lines.Length)
        {
            Match item = ListItem().Match(lines[i]);
            if (!item.Success || item.Groups[1].Length > markerIndent + 3 || char.IsAsciiDigit(item.Groups[2].Value[0]) != ordered || item.Groups[2].Value[^1] != delimiter)
                break;

            int spaces = item.Groups[3].Length;
            int contentIndent = item.Groups[1].Length + item.Groups[2].Length + (spaces is 0 or > 4 ? 1 : spaces);
            var body = new List<string> { item.Groups[4].Value };
            i++;
            bool blank = false;
            while (i < lines.Length)
            {
                string line = lines[i];
                if (line.Trim().Length == 0)
                {
                    blank = true;
                    body.Add(string.Empty);
                    i++;
                    continue;
                }

                if (Indent(line) >= contentIndent)
                {
                    body.Add(Unindent(line, contentIndent));
                    if (blank)
                        loose |= body.Take(body.Count - 2).Any(said => said.Trim().Length > 0) && !ListItem().IsMatch(Unindent(line, contentIndent));
                    blank = false;
                    i++;
                    continue;
                }

                // A line that isn't indented carries on the item's paragraph when no blank line came before it
                if (!blank && !ListItem().IsMatch(line) && Fence(line.TrimStart()) is null && !Heading().IsMatch(line) && !Rule().IsMatch(line))
                {
                    body.Add(line.TrimStart());
                    i++;
                    continue;
                }

                break;
            }

            while (body.Count > 0 && body[^1].Length == 0)
                body.RemoveAt(body.Count - 1);
            items.Add(body);
            if (blank && i < lines.Length && ListItem().Match(lines[i]) is { Success: true } after && after.Groups[1].Length <= markerIndent + 3)
                loose = true;
            if (blank && (i >= lines.Length || !ListItem().IsMatch(lines[i])))
                break;
        }

        string tag = ordered ? "ol" : "ul";
        html.Append('<').Append(tag);
        if (ordered && int.TryParse(first.Groups[2].Value[..^1], out int start) && start != 1)
            html.Append(" start=\"").Append(start).Append('"');
        html.Append(">\n");
        foreach (List<string> body in items)
        {
            html.Append("<li>");
            if (body.Count > 0 && TaskBox().Match(body[0]) is { Success: true } task)
            {
                html.Append(task.Groups[1].Value.Trim().Length > 0 ? "☑ " : "☐ ");
                body[0] = body[0][task.Length..];
            }

            Blocks(html, [.. body], attachments, options, tight: !loose);
            html.Append("</li>\n");
        }

        html.Append("</").Append(tag).Append(">\n");
        return i;
    }

    private static int Indent(string line)
    {
        int columns = 0;
        foreach (char c in line)
        {
            if (c == ' ')
                columns++;
            else if (c == '\t')
                columns += 4 - columns % 4;
            else
                break;
        }

        return columns;
    }

    // Helper method for a line with up to so many columns of its indent taken off
    private static string Unindent(string line, int columns)
    {
        int at = 0, taken = 0;
        while (at < line.Length && taken < columns && line[at] is ' ' or '\t')
        {
            taken += line[at] == '\t' ? 4 - taken % 4 : 1;
            at++;
        }

        return line[at..];
    }

    // Helper method for the fence a line opens code with, null when it doesn't
    private static string? Fence(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~'))
            return null;
        int length = 1;
        while (length < trimmed.Length && trimmed[length] == trimmed[0])
            length++;
        if (length < 3 || trimmed[0] == '`' && trimmed[length..].Contains('`'))
            return null;
        return new string(trimmed[0], length);
    }

    /// <summary>A heading's anchor the way Jupyter makes it, its text with the spaces made dashes.</summary>
    internal static string Slug(string heading)
    {
        string text = Regex.Replace(heading, @"[`*_]|\[([^\]]*)\]\([^)]*\)", match => match.Groups[1].Success ? match.Groups[1].Value : string.Empty);
        return Regex.Replace(text.Trim(), @"\s+", "-");
    }

    /* Inside a block */

    // Helper method for a block's text as inline HTML, its formulas drawn and the rest written out
    private static string Inline(string text, JsonObject? attachments, NotebookHtmlOptions options)
    {
        var html = new StringBuilder();
        foreach (NotebookMath.Piece piece in NotebookMath.Split(text))
            html.Append(piece.Math ? Formula(piece.Text, piece.Display, options) : InlineText(piece.Text, attachments, options));
        return html.ToString();
    }

    private static string InlineText(string text, JsonObject? attachments, NotebookHtmlOptions options)
    {
        var opaque = new List<string>();
        var plain = new StringBuilder(text.Length);
        void Opaque(string html)
        {
            plain.Append(OPAQUE).Append(opaque.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(OPAQUE_END);
            opaque.Add(html);
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next == '\n')
                {
                    Opaque("<br>\n");
                    i += 2;
                    continue;
                }

                if (char.IsAsciiLetterOrDigit(next) || next > 127 || char.IsWhiteSpace(next))
                {
                    plain.Append(Encode("\\"));
                    i++;
                    continue;
                }

                Opaque(Encode(next.ToString()));
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                int spaces = 0;
                while (plain.Length - spaces - 1 >= 0 && plain[plain.Length - spaces - 1] == ' ')
                    spaces++;
                if (spaces >= 2)
                {
                    plain.Length -= spaces;
                    Opaque("<br>\n");
                }
                else
                {
                    plain.Append('\n');
                }

                i++;
                continue;
            }

            if (c == '`')
            {
                int run = 1;
                while (i + run < text.Length && text[i + run] == '`')
                    run++;
                string ticks = new('`', run);
                int end = text.IndexOf(ticks, i + run, StringComparison.Ordinal);
                if (end > 0)
                {
                    string code = text[(i + run)..end].Replace('\n', ' ');
                    if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ')
                        code = code[1..^1];
                    Opaque("<code>" + Encode(code) + "</code>");
                    i = end + run;
                    continue;
                }

                plain.Append(Encode(ticks));
                i += run;
                continue;
            }

            if ((c == '!' && i + 1 < text.Length && text[i + 1] == '[' || c == '[') && LinkAt(text, c == '!' ? i + 1 : i) is { } link)
            {
                if (c == '!')
                    Opaque(Picture(link.Label, link.Target, attachments));
                else
                {
                    string label = Inline(link.Label, attachments, options);
                    Opaque(SafeLink(link.Target) ? $"<a href=\"{Encode(link.Target)}\">{label}</a>" : $"<span class=\"link\">{label}</span>");
                }

                i = link.End;
                continue;
            }

            if (c == '<')
            {
                int end = text.IndexOf('>', i + 1);
                string inside = end > i ? text[(i + 1)..end] : string.Empty;
                if (end > i && (inside.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inside.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || inside.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) && !inside.Any(char.IsWhiteSpace))
                {
                    Opaque($"<a href=\"{Encode(inside)}\">{Encode(inside)}</a>");
                    i = end + 1;
                    continue;
                }

                // Markup a cell has in it, kept for Clean to say what's safe of it
                if (end > i && inside.Length > 0 && (char.IsAsciiLetter(inside[0]) || inside[0] == '/' && inside.Length > 1 && char.IsAsciiLetter(inside[1]) || inside.StartsWith("!--", StringComparison.Ordinal)))
                {
                    Opaque(text[i..(end + 1)]);
                    i = end + 1;
                    continue;
                }

                plain.Append("&lt;");
                i++;
                continue;
            }

            if (c == '&' && Entity().Match(text, i) is { Success: true } entity && entity.Index == i)
            {
                Opaque(entity.Value);
                i += entity.Length;
                continue;
            }

            plain.Append(c switch { '<' => "&lt;", '>' => "&gt;", '&' => "&amp;", '"' => "&quot;", _ => c.ToString() });
            i++;
        }

        string emphasised = Strong().Replace(plain.ToString(), "<strong>$2</strong>");
        emphasised = Struck().Replace(emphasised, "<del>$1</del>");
        emphasised = Emphasis().Replace(emphasised, "<em>$1</em>");
        emphasised = Underscored().Replace(emphasised, "<em>$1</em>");
        return OpaquePattern().Replace(emphasised, match => opaque[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    // Helper method for [label](target "title") starting at a [, null when there isn't one there
    private static (string Label, string Target, int End)? LinkAt(string text, int open)
    {
        int depth = 0, close = -1;
        for (int at = open; at < text.Length; at++)
        {
            if (text[at] == '\\') { at++; continue; }
            if (text[at] == '[') depth++;
            else if (text[at] == ']' && --depth == 0) { close = at; break; }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            return null;
        int end = text.IndexOf(')', close + 2);
        if (end < 0)
            return null;
        string target = text[(close + 2)..end].Trim();
        if (target.StartsWith('<') && target.IndexOf('>') is > 0 and var shut)
            target = target[1..shut];
        else if (target.IndexOf(' ') is > 0 and var space)
            target = target[..space];
        return (text[(open + 1)..close], target, end + 1);
    }

    // Helper method for a picture markdown shows, out of the cell's attachments or already in the page, never fetched
    private static string Picture(string alt, string target, JsonObject? attachments)
    {
        if (target.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
        {
            string name = Uri.UnescapeDataString(target["attachment:".Length..]);
            if (attachments?[name] is JsonObject bundle)
            {
                if (bundle["image/svg+xml"] is { } svg && SvgImage(NotebookFile.Text(svg), alt, null) is { } vector)
                    return vector;
                foreach (string mime in RASTERS)
                {
                    if (bundle[mime] is { } raster && RasterImage(mime, NotebookFile.Text(raster), alt, null) is { } picture)
                        return picture;
                }
            }

            return $"<span class=\"missing\">[{Encode(alt.Length > 0 ? alt : name)}, attachment {Encode(name)} is missing]</span>";
        }

        if (EmbeddedImage(target))
            return $"<img class=\"picture\" src=\"{Encode(target)}\" alt=\"{Encode(alt)}\">";
        string title = SafeLink(target) ? $" title=\"{Encode(target)}\"" : string.Empty;
        return $"<span class=\"missing\"{title}>[{Encode(alt.Length > 0 ? alt : "image")}, not embedded in the notebook]</span>";
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^ {0,3}(=+|-+)[ \t]*$")]
    private static partial Regex Underline();

    [GeneratedRegex(@"^( *)([-*+]|\d{1,9}[.)])( +|$)(.*)$")]
    private static partial Regex ListItem();

    // Bullets and lists starting at 1, the ones that can break into a paragraph
    [GeneratedRegex(@"^ {0,3}([-*+]|1[.)])( +)\S")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"^\[([ xX])\][ \t]+")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"^</?(div|table|thead|tbody|tr|td|th|p|details|summary|section|figure|center|blockquote|pre|ul|ol|dl|h[1-6]|hr|img|svg|iframe|script|style|math)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlock();

    [GeneratedRegex(@"&(#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Entity();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Singleline)]
    private static partial Regex Strong();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline)]
    private static partial Regex Struck();

    [GeneratedRegex(@"\*(?=[^\s*])(.+?)(?<=[^\s*])\*", RegexOptions.Singleline)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![\p{L}\p{N}_])", RegexOptions.Singleline)]
    private static partial Regex Underscored();

    [GeneratedRegex("\u0001([0-9]+)\u0002")]
    private static partial Regex OpaquePattern();
}
