using System.Numerics;

using Hex.Core.Notebooks;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>A table a notebook shows, made as wide as what's in it the first time it's laid out in a module.</summary>
internal sealed class NotebookTableView : Table
{
    public bool Fitted { get; set; }
    protected override bool UsesScrollSpeed => Horizon.Engine.GameEngine.Instance?.Input.Keyboard.Primary != true;

    protected override bool OnScroll(float delta) => Horizon.Engine.GameEngine.Instance?.Input.Keyboard.Primary == true ? false : base.OnScroll(delta);
}

// Markdown written out with its tables drawn as tables, and a DataFrame's HTML as one too. Text round a table stays a
// label of markdown, each table between is the engine's Table, as tall as its rows up to a few dozen and scrolled past that
internal sealed partial class DawnScene
{
    // How many rows a table shows before it scrolls, how tall each is and the size of its text
    private const int TABLE_ROWS = 15;
    private const float TABLE_ROW = 24.0f;
    private const float TABLE_TEXT = 0.13f;

    // Helper method for some markdown as labels and tables, into a panel that's emptied first
    private void WriteMarkdown(StackPanel into, string markdown, string language, List<NotebookPicture>? pictures = null, System.Text.Json.Nodes.JsonObject? attachments = null)
    {
        markdown = NotebookMarkdown.Normalize(markdown);
        foreach (UIComponent child in into.Children.ToList())
            into.Remove(child);

        if (attachments is not null && pictures is not null)
        {
            foreach (var piece in NotebookAttachments.Split(markdown))
            {
                if (piece.Name is { } name && attachments[name] is System.Text.Json.Nodes.JsonObject bundle)
                {
                    NotebookPicture? image = null;
                    if (bundle["image/svg+xml"] is { } svg)
                        image = RenderedPicture(svg, () => NotebookRender.Svg(NotebookFile.Text(svg)), pictures);
                    foreach (string mime in new[] { "image/png", "image/jpeg", "image/gif" })
                        if (image is null && bundle[mime] is { } data)
                            image = OutputPicture(data, NotebookFile.Text(data), null, pictures);
                    if (image is not null) into.Add(image);
                    else into.Add(MarkdownLabel(piece.Text, language));
                }
                else
                {
                    var block = new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4 };
                    WriteMarkdown(block, piece.Text, language, pictures);
                    into.Add(block);
                }
            }
            return;
        }

        if (!NotebookTables.HasTable(markdown))
        {
            WriteMathMarkdown(into, markdown, language, pictures);
            return;
        }

        foreach (var (text, table) in NotebookTables.Split(markdown))
        {
            if (table is not null)
                into.Add(table.MarkdownHeader is not null ? MarkdownTable(table, language, pictures) : TableView(table));
            else if (!string.IsNullOrWhiteSpace(text))
                WriteMathMarkdown(into, text, language, pictures);
        }
    }

    private void WriteMathMarkdown(StackPanel into, string markdown, string language, List<NotebookPicture>? pictures)
    {
        var pieces = NotebookMath.Split(markdown);
        if (pictures is null || !pieces.Any(piece => piece.Math))
        {
            into.Add(MarkdownLabel(markdown, language));
            return;
        }
        // Paragraph boundaries keep headings, lists, and fenced code on the existing Markdown path.
        var paragraph = new List<NotebookMath.Piece>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            string original = string.Concat(paragraph.Select(piece => piece.Math ? "$" + piece.Text + "$" : piece.Text));
            if (paragraph.Any(piece => piece.Math))
            {
                string latex = string.Concat(paragraph.Select(piece => piece.Math ? "$" + piece.Text + "$" : NotebookMath.MarkdownText(piece.Text)));
                if (ProseMathPicture(latex, pictures) is { } rendered)
                    into.Add(rendered);
                else
                    into.Add(MarkdownLabel(original, language));
            }
            else if (!string.IsNullOrWhiteSpace(original))
                into.Add(MarkdownLabel(original, language));
            paragraph.Clear();
        }
        foreach (var piece in pieces)
        {
            if (piece.Display)
            {
                Flush();
                var key = System.Text.Json.Nodes.JsonValue.Create(piece.Text)!;
                if (RenderedPicture(key, () => NotebookRender.Math(piece.Text, transparent: true), pictures, mathInk: true) is { } rendered)
                    into.Add(rendered);
                else
                    into.Add(MarkdownLabel("$$" + piece.Text + "$$", language));
            }
            else if (!piece.Math)
            {
                string[] paragraphs = System.Text.RegularExpressions.Regex.Split(piece.Text, @"\n\n|\n(?=[ \t]*(?:[-*+] |\d+\. ))");
                for (int i = 0; i < paragraphs.Length; i++)
                {
                    if (i > 0) Flush();
                    paragraph.Add(new NotebookMath.Piece(paragraphs[i]));
                }
            }
            else paragraph.Add(piece);
        }
        Flush();
    }

    private static Label MarkdownLabel(string markdown, string language) => new()
    {
        Text = markdown,
        Markdown = true,
        Wrap = true,
        Align = Origin.TopLeft,
        Anchor = Origin.TopLeft,
        Pivot = Origin.TopLeft,
        TextScale = 0.16f,
        CodeLanguage = language
    };

    // Helper method for a table drawn, as tall as its rows and its header up to TABLE_ROWS of them
    private NotebookTablePanel TableView(NotebookTable table)
    {
        int shown = Math.Min(table.Rows.Count, TABLE_ROWS);
        var view = new NotebookTableView
        {
            Columns = [.. table.Header.Select((title, i) => new UITableColumn(title) { Align = table.Align[i] switch { 'r' => 1.0f, 'c' => 0.5f, _ => 0.0f } })],
            Rows = [.. table.Rows.Select(row => row.Select(text => new UITableCell(text)).ToArray())],
            RowHeight = TABLE_ROW,
            TextScale = TABLE_TEXT,
            FreezeFirst = table.Header.Count > 2,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Size = new Vector2(600.0f, (shown + 1) * TABLE_ROW + 10.0f)
        };
        view.Refresh();
        return TableWithTools(view);
    }

    private NotebookTablePanel MarkdownTable(NotebookTable table, string language, List<NotebookPicture>? pictures)
    {
        var grid = new NotebookMarkdownTable();
        var rows = new List<List<string>> { table.MarkdownHeader! };
        rows.AddRange(table.MarkdownRows!);
        grid.Weights = Enumerable.Range(0, table.Header.Count).Select(column =>
            Math.Clamp(MathF.Sqrt(rows.Max(row => row[column].Length) + 1), 1.5f, 14)).ToArray();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = grid.Add(new StackPanel { Direction = UIDirection.Horizontal, Stretch = true, Spacing = 1, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            var cells = new StackPanel[table.Header.Count];
            for (int c = 0; c < cells.Length; c++)
            {
                cells[c] = row.Add(new StackPanel
                {
                    Direction = UIDirection.Vertical, Spacing = 2, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft,
                    Padding = new UIEdges(6), Color = compositor.Skin?.FieldColor ?? Vector4.Zero
                });
                string source = rows[r][c];
                WriteMathMarkdown(cells[c], r == 0 ? "**" + source + "**" : source, language, pictures);
            }
            grid.Cells.Add(cells);
        }
        // The native table retains the plain export model; Markdown rows themselves remain document components.
        var export = new NotebookTableView
        {
            Columns = [.. table.Header.Select(title => new UITableColumn(title))],
            Rows = [.. table.Rows.Select(row => row.Select(text => new UITableCell(text)).ToArray())]
        };
        export.Refresh();
        return TableWithTools(export, grid);
    }

    internal static void FitNotebookMarkdownCell(StackPanel cell, float width, float zoom) => FitWritten(cell, width, zoom, keepWidth: true);

    // Helper method for a DataFrame (or any HTML with a table in it) as a table with what pandas said under it, its
    // shape, null when there's no table to read out of it
    private StackPanel? OutputHtmlTable(string html)
    {
        if (NotebookTables.FromHtml(html, out string? caption) is not { } table)
            return null;

        var panel = new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 2.0f };
        panel.Add(TableView(table));
        if (caption is not null)
            panel.Add(new Label { Text = caption, Dim = true, Align = Origin.TopLeft, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, TextScale = 0.125f });
        return panel;
    }

    // Helper method for markdown and tables as wide as there's room for, a table no wider than its columns
    private static void FitWritten(StackPanel written, float width, float zoom = 1, bool keepWidth = false)
    {
        if (!keepWidth) written.Size = written.Size with { X = width };
        for (int index = 0; index < written.Children.Count; index++)
        {
            UIComponent piece = written.Children[index];
            switch (piece)
            {
                case NotebookMarkdownTable markdown:
                    markdown.Fit(width, zoom);
                    break;
                case NotebookTablePanel tools:
                    bool compact = width < 232;
                    tools.CopyCsv.Label = compact ? string.Empty : tools.CopyLabel;
                    tools.ExportCsv.Label = compact ? string.Empty : tools.ExportLabel;
                    tools.CopyCsv.Size = tools.CopyCsv.Size with { X = compact ? 28 : 104 };
                    tools.ExportCsv.Size = tools.ExportCsv.Size with { X = compact ? 28 : 116 };
                    FitWritten(tools, width, zoom);
                    break;
                case NotebookPicture picture:
                    picture.Reflow?.Invoke(width, zoom);
                    picture.Size = picture.Natural * MathF.Min(picture.Reflow is null ? zoom : 1, width / MathF.Max(1, picture.Natural.X));
                    break;
                case NotebookTableView table:
                    if (table.TextScale != TABLE_TEXT * zoom)
                    {
                        table.TextScale = TABLE_TEXT * zoom;
                        table.RowHeight = TABLE_ROW * zoom;
                        table.Size = table.Size with { Y = (Math.Min(table.Rows.Count, TABLE_ROWS) + 1) * TABLE_ROW * zoom + 10 };
                        table.Fitted = false;
                    }
                    if (!table.Fitted && table.Module is not null)
                    {
                        table.FitColumns(48.0f * zoom, 280.0f * zoom);
                        table.Fitted = true;
                    }

                    // Avoid boxing the column enumerator in the native AOT build's idle layout path.
                    float columns = 12.0f;
                    for (int column = 0; column < table.Columns.Count; column++)
                        columns += table.Columns[column].Width;
                    table.Size = table.Size with { X = MathF.Min(width, columns) };
                    table.FreezeFirst = table.Columns.Count > 2 && width > table.Columns[0].Width + 80;
                    break;
                case Label label:
                    label.TextScale = (label.Dim ? 0.125f : 0.16f) * zoom;
                    label.Size = label.Size with { X = width - 8.0f };
                    break;
                case StackPanel inner:
                    FitWritten(inner, width, zoom);
                    break;
            }
        }
    }
}
