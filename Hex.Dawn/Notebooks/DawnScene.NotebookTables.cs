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
    private void WriteMarkdown(StackPanel into, string markdown, string language)
    {
        foreach (UIComponent child in into.Children.ToList())
            into.Remove(child);

        if (!NotebookTables.HasTable(markdown))
        {
            into.Add(MarkdownLabel(markdown, language));
            return;
        }

        foreach (var (text, table) in NotebookTables.Split(markdown))
        {
            if (table is not null)
                into.Add(TableView(table));
            else if (!string.IsNullOrWhiteSpace(text))
                into.Add(MarkdownLabel(text, language));
        }
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
    private static NotebookTableView TableView(NotebookTable table)
    {
        int shown = Math.Min(table.Rows.Count, TABLE_ROWS);
        var view = new NotebookTableView
        {
            Columns = [.. table.Header.Select(title => new UITableColumn(title))],
            Rows = [.. table.Rows.Select(row => row.Select(text => new UITableCell(text)).ToArray())],
            RowHeight = TABLE_ROW,
            TextScale = TABLE_TEXT,
            FreezeFirst = table.Header.Count > 2,
            Anchor = Origin.TopLeft,
            Pivot = Origin.TopLeft,
            Size = new Vector2(600.0f, (shown + 1) * TABLE_ROW + 10.0f)
        };
        view.Refresh();
        return view;
    }

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
    private static void FitWritten(StackPanel written, float width)
    {
        written.Size = written.Size with { X = width };
        foreach (UIComponent piece in written.Children)
        {
            switch (piece)
            {
                case NotebookTableView table:
                    if (!table.Fitted && table.Module is not null)
                    {
                        table.FitColumns(48.0f, 280.0f);
                        table.Fitted = true;
                    }

                    float columns = table.Columns.Sum(column => column.Width) + 12.0f;
                    table.Size = table.Size with { X = MathF.Min(width, columns) };
                    break;
                case Label label:
                    label.Size = label.Size with { X = width - 8.0f };
                    break;
                case StackPanel inner:
                    FitWritten(inner, width);
                    break;
            }
        }
    }
}
