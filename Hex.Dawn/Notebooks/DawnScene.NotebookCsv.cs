using System.Numerics;
using System.Text;
using Hex.Core.Notebooks;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed class NotebookTablePanel(NotebookTableView table) : StackPanel
{
    public NotebookTableView Table { get; } = table;
    public Button CopyCsv { get; set; } = null!;
    public Button ExportCsv { get; set; } = null!;
    public Action<string> ExportTo { get; set; } = null!;
    public Func<string> Csv { get; set; } = null!;
    public string CopyLabel { get; set; } = string.Empty;
    public string ExportLabel { get; set; } = string.Empty;
}

internal sealed partial class DawnScene
{
    private NotebookTablePanel TableWithTools(NotebookTableView view, UIComponent? body = null)
    {
        var panel = new NotebookTablePanel(view)
        {
            Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 2
        };
        var strip = panel.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4,
            Size = new Vector2(240, 26)
        });
        panel.CopyCsv = strip.Add(new Button
        {
            Label = Said("notebook.copy_csv"), Icon = "copy", Tooltip = Said("notebook.copy_csv_tip"), Size = new Vector2(104, 26)
        });
        panel.ExportCsv = strip.Add(new Button
        {
            Label = Said("notebook.export_csv"), Icon = "save", Tooltip = Said("notebook.export_csv_tip"), Size = new Vector2(116, 26)
        });
        panel.CopyLabel = panel.CopyCsv.Label;
        panel.ExportLabel = panel.ExportCsv.Label;
        string Csv() => NotebookCsv.Write(view.Columns.Select(column => column.Title),
            view.RowOrder.Select(row => Enumerable.Range(0, view.Columns.Count).Select(column => column < view.Rows[row].Length ? view.Rows[row][column].Text ?? string.Empty : string.Empty)));
        panel.Csv = Csv;
        panel.CopyCsv.OnPressed = () => UIClipboard.Copy(Csv());
        panel.ExportTo = path => WriteNotebookCsv(path, Csv());
        panel.ExportCsv.OnPressed = () =>
        {
            string csv = Csv();
            string name = Path.GetFileNameWithoutExtension(document.Path ?? document.Name) + "-table.csv";
            string directory = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path))! : browseDirectory;
            Ask(HexDialogs.SaveFile(directory, name, [("CSV", "*.csv")], DialogHost), target =>
                WriteNotebookCsv(Path.HasExtension(target) ? target : target + ".csv", csv));
        };
        panel.Add(body ?? view);
        return panel;
    }

    private void WriteNotebookCsv(string path, string csv)
    {
        try
        {
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            Notify(Said("notebook.csv_exported", ("name", Path.GetFileName(path))));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Notify(Said("notebook.csv_failed", ("why", error.Message)), error: true);
        }
    }
}
