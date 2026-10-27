using System.Numerics;

using Horizon.Content;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The table of a file of code, the other way of looking at one beside its code (F7 between them). An object whose values
// are objects (Fighter2D's moves, characters, maps) is a row each and a column for every key any of them has, the way a
// designer of a fighting game looks at frame data. A file of texts (let strings) is a row a key and a column a language,
// every language next to it in its folder, what one hasn't got in red. A cell written as itself in the file on screen is
// edited where it is with a double click, the same way as in the values.
internal sealed partial class HexRunning
{
    private const float KEY_COLUMN = 180.0f, VALUE_COLUMN = 120.0f, TEXT_COLUMN = 320.0f;

    private Table dataTable = null!;

    // What each cell of the table is about, by its row and column, for editing it
    private readonly Dictionary<(int Row, int Column), SourceLocation> tableCells = [];

    // What the table was last made of, so it is only made again when that changes
    private HexValues? tabledFrom;

    /// <summary>Which declaration of a run is shown as a table, and whether it is a file of texts. Null if there is none.</summary>
    private static (string Name, ObjectValue Value, bool Texts)? TableOf(HexValues of)
    {
        foreach (var (name, value) in of.Declared)
        {
            if (value is not ObjectValue obj || obj.Properties.Count == 0)
                continue;

            if (name == "strings")
                return (name, obj, true);

            int objects = obj.Properties.Values.Count(inner => inner is ObjectValue);
            if (objects >= 2 && objects * 2 >= obj.Properties.Count)
                return (name, obj, false);
        }

        return null;
    }

    /// <summary>Whether the document on screen has a table to show, which is when its view buttons have one.</summary>
    internal bool HasTable => document.IsText && values is { } shown && valuesOf == document && TableOf(shown) is not null;

    /// <summary>
    /// Whether the Code and Table buttons are up, which is once the file on screen has had a table, and stay up while a
    /// mistake in it has it come to nothing for a moment, Table greyed out meanwhile. They came and went with the table,
    /// and the tabs beside them were laid out a tick behind, so the code jumped sideways as a line went red and back.
    /// </summary>
    internal bool ShowsTableButtons => document.IsText && (HasTable || tableSeen == document);

    // The file that last had a table, its buttons stay up while it doesn't for a moment
    private HexDocument? tableSeen;

    // Helper method to put the Code and Table buttons up or away, and Table greyed out while there's no table to show
    internal void ShowTableButtons(int extensionButtons)
    {
        if (HasTable)
            tableSeen = document;
        layout.Get<UIComponent>("views").Visible = !document.IsText || ShowsTableButtons || extensionButtons > 0;
        layout.Get<Button>("table_view").Enabled = !document.IsText || HasTable;
    }

    private void BuildTable()
    {
        dataTable = layout.Get<Table>("data_table");
        dataTable.Visible = false;
        dataTable.OnActivated = ActivateCell;
        layout.Get<Button>("table_view").OnPressed = () => window.SwitchView(HexView.Table);
    }

    /// <summary>Every tick while the table is up, made again when the values it shows were.</summary>
    internal void UpdateTable()
    {
        if (!dataTable.Visible || values is not { } shown || valuesOf != document || ReferenceEquals(shown, tabledFrom))
            return;

        tabledFrom = shown;
        RebuildTable(shown);
    }

    private void RebuildTable(HexValues shown)
    {
        tableCells.Clear();
        dataTable.Columns = [];
        dataTable.Rows = [];

        if (TableOf(shown) is not { } table)
        {
            dataTable.Columns.Add(new UITableColumn(context.Strings.Said("table.nothing"), 600.0f));
            dataTable.Refresh();
            return;
        }

        if (table.Texts)
            TextsTable(shown, table.Value);
        else
            DataTable(shown, table.Name, table.Value);

        dataTable.FitColumns();
        dataTable.Refresh();
    }

    /// <summary>What a cell is written in, by what is in it, the way the code colours the same thing.</summary>
    private Vector4? CellColor(IRuntimeValue value) => value switch
    {
        ObjectValue or ListValue => context.Panels.Dim,
        NumberValue => SymbolNumber,
        BooleanValue or NullValue => SymbolConstant,
        _ => null
    };

    /// <summary>A row for every object in an object, a column for every key any of them has.</summary>
    private void DataTable(HexValues shown, string name, ObjectValue rows)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IRuntimeValue row in rows.Properties.Values)
        {
            if (row is not ObjectValue obj) continue;
            foreach (string key in obj.Properties.Keys)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
        }

        dataTable.Columns.Add(new UITableColumn(name, KEY_COLUMN));
        foreach (string key in keys)
            dataTable.Columns.Add(new UITableColumn(key, VALUE_COLUMN) { Tooltip = key });

        foreach (var (rowName, value) in rows.Properties)
        {
            var cells = new UITableCell[keys.Count + 1];
            cells[0] = new UITableCell(rowName) { Tooltip = $"{name}.{rowName}" };

            if (value is ObjectValue obj)
            {
                for (int k = 0; k < keys.Count; k++)
                {
                    if (!obj.Properties.TryGetValue(keys[k], out IRuntimeValue? inner))
                        continue;

                    cells[k + 1] = new UITableCell(inner is StringValue said ? said.Value : Summarise(inner), CellColor(inner))
                    {
                        Tooltip = $"{name}.{rowName}.{keys[k]} = {HexEvaluation.Shorten(HexEvaluation.Describe(inner), 200)}"
                    };

                    if (shown.Sources.TryGetKey(obj, keys[k], out var at))
                        tableCells[(dataTable.Rows.Count, k + 1)] = at;
                }
            }

            if (shown.Sources.TryGetKey(rows, rowName, out var rowAt))
                tableCells[(dataTable.Rows.Count, 0)] = rowAt;

            dataTable.Rows.Add(cells);
        }
    }

    /// <summary>
    /// A row for every key of the texts, a column for this language and every other one next to it in its folder (a file
    /// that says let strings), what a language hasn't got in red.
    /// </summary>
    private void TextsTable(HexValues shown, ObjectValue strings)
    {
        var languages = new List<(string Name, IReadOnlyDictionary<string, string> Texts)>
        {
            (document.Name, Flatten(strings))
        };

        if (document.Path is { } path && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
        {
            foreach (string other in Directory.GetFiles(folder, "*.hor").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), PlatformPaths.Comparison))
                    continue;

                try
                {
                    var texts = Localisation.ReadFile(other);
                    if (texts.Count > 0)
                        languages.Add((Path.GetFileNameWithoutExtension(other), texts));
                }
                catch (Exception)
                {
                    // Not a file of texts, or one that doesn't run, it isn't a language
                }
            }
        }

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, texts) in languages)
        {
            foreach (string key in texts.Keys)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
        }

        dataTable.Columns.Add(new UITableColumn(context.Strings.Said("table.key"), KEY_COLUMN + 60.0f));
        foreach (var (name, _) in languages)
            dataTable.Columns.Add(new UITableColumn(name, TEXT_COLUMN));

        Vector4 missing = context.Panels.Error with { W = 0.18f };
        foreach (string key in keys)
        {
            var cells = new UITableCell[languages.Count + 1];
            cells[0] = new UITableCell(key) { Tooltip = $"@{key}" };

            for (int l = 0; l < languages.Count; l++)
            {
                if (languages[l].Texts.TryGetValue(key, out string? text))
                    cells[l + 1] = new UITableCell(text.Replace('\n', ' ')) { Tooltip = text };
                else
                    cells[l + 1] = new UITableCell(context.Strings.Said("table.missing")) { Color = context.Panels.Error, Fill = missing, Tooltip = context.Strings.Said("table.missing_in", ("language", languages[l].Name)) };
            }

            if (KeyIn(shown, strings, key) is { } at)
                tableCells[(dataTable.Rows.Count, 1)] = at;

            dataTable.Rows.Add(cells);
        }
    }

    /// <summary>Every text of an object of them by its key with dots, the way a layout names one.</summary>
    private static Dictionary<string, string> Flatten(ObjectValue strings)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(ObjectValue at, string prefix)
        {
            foreach (var (key, value) in at.Properties)
            {
                string path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (value is ObjectValue inner) Walk(inner, path);
                else if (value is StringValue text) flat[path] = text.Value;
                else if (value is ListValue lines) flat[path] = string.Join('\n', lines.Items.Select(line => Values.Text(line)));
            }
        }

        Walk(strings, string.Empty);
        return flat;
    }

    /// <summary>Where a text of the file on screen was written, by its key with dots.</summary>
    private static SourceLocation? KeyIn(HexValues shown, ObjectValue strings, string key)
    {
        string[] steps = key.Split('.');
        ObjectValue at = strings;
        for (int i = 0; i < steps.Length; i++)
        {
            if (i == steps.Length - 1)
                return shown.Sources.TryGetKey(at, steps[i], out var found) ? found : null;

            if (!at.Properties.TryGetValue(steps[i], out IRuntimeValue? inner) || inner is not ObjectValue next)
                return null;
            at = next;
        }

        return null;
    }

    /// <summary>A double click on a cell. One written as itself in this file is edited where it is, anything else goes to the code.</summary>
    internal void ActivateCell(int row, int column)
    {
        if (!tableCells.TryGetValue((row, column), out SourceLocation at))
            return;

        bool here = at.File is null || (document.Path is { } open && string.Equals(Path.GetFullPath(at.File), Path.GetFullPath(open), PlatformPaths.Comparison));
        if (column == 0 || !here || !at.Literal || !at.Value.IsKnown)
        {
            window.SwitchView(HexView.Code);
            code.GoTo(at.Span.Line - 1, at.Span.Column - 1);
            code.Focus();
            return;
        }

        if (valuesAsked != (document, code.Document.Version))
        {
            context.Notices.Say(context.Strings.Said("values.behind"), error: true);
            return;
        }

        StartEditing(() => dataTable.CellBounds(row, column), at.Value);
    }
}
