using System.Numerics;
using System.Text;

using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Hex.Dawn;

// The right side for a file of code. What the file comes to when it is run, as a tree, a moment after it was last
// typed into, the way the game that reads it would see it. HIDL is a language of values first, a move list is right
// when the moves it comes to are, and this is where that is seen. A click on a row goes to where it is in the code.
internal sealed partial class HexRunning
{
    private readonly DawnContext context;
    private readonly IEditorWindow window;

    // What of the editor is used everywhere in here, by the names it had in the scene
    private HexDocument document => context.Documents.Current;
    private CodeEditor code => window.Code;
    private bool LayoutTools => document.InDesigner;
    private UILayout layout => window.Layout;
    private UIModule chrome => window.Chrome;
    private UICompositor compositor => window.Compositor;

    // The right side's panels, which the designer's properties are in when a layout is on screen
    private readonly StackPanel inspector;
    private readonly ScrollPanel inspectorScroll;

    /// <summary>A file of HIDL ran without going wrong, by its full path (the extensions hear of it as on_run).</summary>
    public event Action<string>? Ran;

    /// <summary>
    /// A file of HIDL run as it is typed, what it comes to on the right side, as a table, in the console and the runner,
    /// and what that says is wrong with it under its code.
    /// </summary>
    public HexRunning(DawnContext with)
    {
        context = with;
        window = with.Window;
        inspector = layout.Get<StackPanel>("inspector");
        inspectorScroll = layout.Get<ScrollPanel>("inspector_scroll");
        BuildValuesTree();
        BuildTable();
        BuildConsole();
        context.Documents.AddDiagnostics(DiagnosticsOf);
    }

    /// <summary>The run of the file on screen, null until it ran, and the file it was of.</summary>
    internal HexValues? LastRun => values;
    internal HexDocument? LastRunOf => valuesOf;

    /// <summary>The tree the values are shown in, and the table, for the window to size.</summary>
    internal TreeView ValuesTree => valuesTree;
    internal Table Table => dataTable;

    // The lints of the last clean run, worked out once a run
    private HexValues? linted;
    private List<UICodeDiagnostic> lints = [];

    /// <summary>What the last run of a document says is wrong with it, its lints and where running it went wrong.</summary>
    private IReadOnlyList<UICodeDiagnostic>? DiagnosticsOf(HexDocument of)
    {
        if (values is null || valuesOf != of)
            return null;

        var found = new List<UICodeDiagnostic>();
        if (values is { Error: null } clean)
        {
            if (!ReferenceEquals(linted, clean))
            {
                linted = clean;
                lints = HexLints.Lint(clean, of.Path, context.Strings.Said);
            }

            found.AddRange(lints);
        }

        if (values is { Error: { } error, ErrorAt.Span.IsKnown: true } ran
            && (ran.ErrorAt.File is null || (of.Path is { } path && string.Equals(Path.GetFullPath(ran.ErrorAt.File), Path.GetFullPath(path), PlatformPaths.Comparison))))
        {
            found.Add(new UICodeDiagnostic(ran.ErrorAt.Span.Line - 1, ran.ErrorAt.Span.Column - 1, Math.Max(1, ran.ErrorAt.Span.Length), error));
        }

        return found;
    }

    // How long the code has to be left alone before it is run again, and the most rows the tree shows
    private const float VALUES_AFTER = 0.35f;
    private const int MAX_VALUE_ROWS = 600;

    // How few things a file declares for them to come open the first time it is looked at
    private const int FEW_DECLARED = 4;

    private readonly HexEvaluation evaluation = new();

    // What the file on screen came to the last time it was run, and which file and which version of it that was
    private HexValues? values;
    private HexDocument? valuesOf;
    private (HexDocument? Document, int Version) valuesAsked = (null, -1);

    // How long since the code last changed, and which version that was
    private (HexDocument? Document, int Version) valuesSeen = (null, -1);
    private float valuesQuiet;

    // The rows of the tree that are open, by their path ("moves.jab.phases")
    private readonly HashSet<string> valuesOpen = new(StringComparer.Ordinal);

    // The tree the values are shown in, on the right where the properties are in the designer
    private TreeView valuesTree = null!;

    /// <summary>Whether the file on screen is one whose values are shown, any file of HIDL that isn't a layout.</summary>
    internal bool ShowsValues => document.IsText && ReferenceEquals(code.Highlighter, Horizon.UI.Scripting.CodeLanguages.Hidl);

    /// <summary>
    /// Every tick. Runs the file again once it was left alone for a moment after a change, and takes in what the last run
    /// came to when it is done.
    /// </summary>
    internal void UpdateValues(float dt)
    {
        if (evaluation.TakeFinished() is { } done)
        {
            // What went wrong says the name it most likely meant, when it named one a letter or two off
            if (done.Error is { } failed && valuesAsked.Document is { } asked)
                done = done with { Error = failed + HexFixes.LikelyFix(failed, asked.Editor.Text) };

            // A file seen for the first time comes open a level, when it declares few enough things for that to be the
            // way into it (a move list is one let of forty moves)
            if (valuesAsked.Document != valuesOf && done.Declared.Count <= FEW_DECLARED)
            {
                foreach (var (name, _) in done.Declared)
                    valuesOpen.Add(name);
            }

            // The table button comes and goes with whether what the file comes to is a table
            bool hadTable = HasTable;

            values = done;
            valuesOf = valuesAsked.Document;
            if (HasTable != hadTable)
                layout.Get<UIComponent>("views").Visible = !document.IsText || HasTable;

            // What went wrong running it is underlined where it went wrong, with what doesn't parse and the rest
            if (valuesOf is { } ran)
            {
                context.Documents.DiagnosticsChanged(ran);
                ShowInlineValues(ran, done);
                if (ran.Path is { } runPath && done.Error is null)
                    Ran?.Invoke(Path.GetFullPath(runPath));
            }
            if (!LayoutTools)
                window.RefreshRight();
        }

        if (!ShowsValues)
            return;

        var now = (document, code.Document.Version);
        if (now != valuesSeen)
        {
            // Another file is run straight away, a change waits for the typing to stop
            valuesQuiet = valuesSeen.Document == document ? 0.0f : VALUES_AFTER;
            valuesSeen = now;
        }

        valuesQuiet += dt;
        if (valuesQuiet >= VALUES_AFTER && valuesAsked != now)
        {
            valuesAsked = now;
            evaluation.Run(code.Text, document.Path is { } path ? Path.GetFullPath(path) : null, FolderOf(document), OpenTexts());
        }
    }

    /// <summary>
    /// The value every let of the file that was worked out came to, written faintly at the end of its line (Light Table's
    /// inline results), `let reach = base * 1.5;   = 96`. A let written as its value already, a text or a number, says
    /// nothing, and an object or a list is the values' to show.
    /// </summary>
    private void ShowInlineValues(HexDocument ran, HexValues done)
    {
        string? own = ran.Path is { } path ? Path.GetFullPath(path) : null;
        var hints = new List<UICodeHint>();
        foreach (var (name, value) in done.Declared)
        {
            if (!done.Sources.TryGetDeclaration(name, out var at) || at.Literal || !at.Span.IsKnown)
                continue;
            if (at.File is { } file && own is not null && !Path.GetFullPath(file).Equals(own, PlatformPaths.Comparison))
                continue;
            if (value is ObjectValue or ListValue or FunctionValue or AnonymousFunctionValue)
                continue;

            hints.Add(new UICodeHint(at.Span.Line - 1, $"= {Summarise(value)}"));
        }

        ran.Editor.InlineHints = hints;
    }

    /// <summary>The folder a document's includes are looked for in.</summary>
    internal string? FolderOf(HexDocument of) => of.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : context.Windows.BrowseFolder;

    /// <summary>What every open file of code says right now by its full path, for an include of one that isn't saved yet.</summary>
    private Dictionary<string, string> OpenTexts()
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (HexDocument open in context.Documents.All)
        {
            if (open.IsText && open.Path is { } path)
                texts[Path.GetFullPath(path)] = open.Editor.Text;
        }

        return texts;
    }

    /// <summary>A value in the tree on the right, where it is in the value of the file, and where it was written.</summary>
    private sealed record ValueTag(string Path, IRuntimeValue Value, SourceLocation? At, Action? Pressed = null);

    /// <summary>
    /// The right side for code. What the file comes to, as a tree the values view paints itself (a TreeView), with what is
    /// wrong with the file as its first rows. Anything that isn't HIDL, or a layout, says why there is nothing in its place.
    /// </summary>
    internal void RebuildCodeInspector()
    {
        // The console is running a line in what the values show, which can change them under the rows being made. They
        // are made once it is done, see UpdateConsole
        if (evaluation.Answering)
            return;

        foreach (var child in inspector.Children.ToArray())
            inspector.Remove(child);

        bool tree = ShowsValues && values is not null && valuesOf == document;
        valuesTree.Visible = tree;
        inspectorScroll.Visible = !tree;

        if (!tree)
        {
            window.Hint(inspector, !ShowsValues ? document.IsText ? context.Strings.Said("values.not_hidl") : context.Strings.Said("values.layout") : context.Strings.Said("values.running")).Wrap = true;
            return;
        }

        HexValues shown = values!;
        var items = new List<UITreeItem>();

        foreach (UICodeDiagnostic error in code.SyntaxErrors)
            items.Add(ProblemItem($"line {error.Line + 1}: {error.Message}", "error", context.Panels.Error, () => GoToCode(error.Line, error.Column)));

        foreach (UICodeDiagnostic warning in code.Diagnostics)
        {
            items.Add(ProblemItem($"line {warning.Line + 1}: {warning.Message}", warning.Warning ? "warning" : "error",
                warning.Warning ? context.Panels.Warning : context.Panels.Error, () => GoToCode(warning.Line, warning.Column)));
        }

        if (shown.Error is { } problem && !shown.ErrorAt.Span.IsKnown)
            items.Add(ProblemItem(problem, "error", context.Panels.Error, null));

        items.Add(new UITreeItem(context.Strings.Said("values.summary", ("count", shown.Declared.Count.ToString()), ("time", shown.Milliseconds.ToString("0.0"))), "info")
        {
            Color = context.Panels.Dim
        });

        if (shown.Declared.Count == 0)
            items.Add(new UITreeItem(context.Strings.Said("values.nothing")) { Color = context.Panels.Dim });

        foreach (var (name, value) in shown.Declared)
            items.Add(ValueItem(name, name, value, shown.Sources.TryGetDeclaration(name, out var declared) ? declared : null));

        // The same row is still selected after the values come in again, by its path
        string? was = (valuesTree.Selected?.Tag as ValueTag)?.Path;
        valuesTree.Items = items;
        if (was is not null)
            valuesTree.Selected = Find(items, was);
    }

    private static UITreeItem? Find(List<UITreeItem>? items, string path)
    {
        if (items is null) return null;
        foreach (UITreeItem item in items)
        {
            if (item.Tag is ValueTag tag && tag.Path == path) return item;
            if (item.Expanded && Find(item.Children, path) is { } inside) return inside;
        }

        return null;
    }

    private UITreeItem ProblemItem(string text, string icon, Vector4 color, Action? pressed) =>
        new(text, icon) { IconColor = color, Color = color, Tooltip = text, Tag = new ValueTag(string.Empty, Values.Null, null, pressed) };

    internal void GoToCode(int line, int column)
    {
        code.GoTo(line, column);
        code.Focus();
    }

    /// <summary>
    /// Helper method to make the row of a value. What is inside of it is only made when it is opened, a move list of forty
    /// moves of a dozen keys each is forty rows until somebody looks in one.
    /// </summary>
    private UITreeItem ValueItem(string name, string path, IRuntimeValue value, SourceLocation? at)
    {
        int inside = value switch
        {
            ObjectValue obj => obj.Properties.Count,
            ListValue list => list.Count,
            _ => 0
        };

        var item = new UITreeItem(name, ValueIcon(value))
        {
            Detail = Summarise(value),
            IconColor = ValueColor(value),
            Tooltip = ValueTooltip(path, value, at),
            Tag = new ValueTag(path, value, at),
            Expandable = inside > 0,
            Expanded = inside > 0 && valuesOpen.Contains(path)
        };

        if (item.Expanded)
            FillValue(item);

        return item;
    }

    /// <summary>Helper method to make the rows inside of a value, as it is opened.</summary>
    private void FillValue(UITreeItem item)
    {
        if (item.Children is not null || item.Tag is not ValueTag tag || values is not { } shown)
            return;

        item.Children = [];
        switch (tag.Value)
        {
            case ObjectValue obj:
                foreach (var (key, inner) in obj.Properties)
                    item.Children.Add(ValueItem(key, $"{tag.Path}.{key}", inner, shown.Sources.TryGetKey(obj, key, out var keyAt) ? keyAt : null));
                break;

            case ListValue list:
                for (int i = 0; i < list.Count && i < MAX_VALUE_ROWS; i++)
                    item.Children.Add(ValueItem($"[{i}]", $"{tag.Path}[{i}]", list[i], shown.Sources.TryGetItem(list, i, out var itemAt) ? itemAt : null));
                break;
        }
    }

    /// <summary>Helper method to set the values tree up, once, as the editor is made.</summary>
    private void BuildValuesTree()
    {
        valuesTree = layout.Get<TreeView>("values_tree");
        valuesTree.Visible = false;

        // Dragged wider by its edge like the properties, the two are the one side
        window.Grip(valuesTree, UIResize.Left, left: false);
        layout.Get<Button>("hide_right").OnPressed = () => context.Panels.PropertiesShown = !context.Panels.PropertiesShown;
        valuesTree.OnExpanding = FillValue;
        valuesTree.OnActivated = ActivateValue;
        valuesTree.OnToggled = item =>
        {
            if (item.Tag is ValueTag tag && !valuesOpen.Remove(tag.Path))
                valuesOpen.Add(tag.Path);
        };
        valuesTree.OnPressed = item =>
        {
            if (item.Tag is not ValueTag tag)
                return;

            if (tag.Pressed is { } pressed)
                pressed();
            else if (tag.Path.Length > 0)
                GoToValue(tag.Path, tag.At);
        };
        valuesTree.OnItemContextMenu = (item, _) =>
        {
            if (item.Tag is ValueTag { Path.Length: > 0 } tag)
                ShowValueMenu(tag.Path, tag.Value);
        };
    }

    /// <summary>The colour of the icon of a value, the way VS Code colours what a symbol is. A colour is its own colour.</summary>
    private static Vector4 ValueColor(IRuntimeValue value) => value switch
    {
        ObjectValue => SymbolObject,
        ListValue => SymbolList,
        StringValue => SymbolText,
        NumberValue => SymbolNumber,
        BooleanValue or NullValue => SymbolConstant,
        Vector4Value colour => Opaque(colour.Value),
        Vector2Value or Vector3Value => SymbolField,
        _ when Values.IsFunction(value) => SymbolList,
        _ => SymbolField
    };

    /// <summary>A colour as it is drawn, written 0 to 1 or 0 to 255 (Fighter2D's maps are), without its alpha.</summary>
    private static Vector4 Opaque(Vector4 colour)
    {
        float most = MathF.Max(colour.X, MathF.Max(colour.Y, colour.Z));
        Vector4 drawn = most > 1.0f ? colour / 255.0f : colour;
        return drawn with { W = 1.0f };
    }

    internal static readonly Vector4 SymbolColour = new(0.86f, 0.45f, 0.7f, 1.0f);
    internal static readonly Vector4 SymbolObject = new(0.93f, 0.62f, 0.16f, 1.0f);
    internal static readonly Vector4 SymbolList = new(0.69f, 0.5f, 0.84f, 1.0f);
    internal static readonly Vector4 SymbolText = new(0.81f, 0.57f, 0.47f, 1.0f);
    internal static readonly Vector4 SymbolNumber = new(0.71f, 0.81f, 0.66f, 1.0f);
    internal static readonly Vector4 SymbolConstant = new(0.34f, 0.61f, 0.84f, 1.0f);
    internal static readonly Vector4 SymbolField = new(0.46f, 0.75f, 1.0f, 1.0f);

    /// <summary>
    /// Goes to where a value was written, which the run of the file knows (an included file is opened), or failing that
    /// where the outline of the file puts its path.
    /// </summary>
    internal void GoToValue(string path, SourceLocation? at)
    {
        if (at is { Span.IsKnown: true } known)
        {
            if (known.File is { } file && (document.Path is null || !string.Equals(Path.GetFullPath(file), Path.GetFullPath(document.Path), PlatformPaths.Comparison)))
                context.Documents.OpenAt(file, known.Span.Line - 1, known.Span.Column - 1);
            else
                code.GoTo(known.Span.Line - 1, known.Span.Column - 1);
            return;
        }

        if (LineOf(path) is { } line)
            code.GoTo(line.Line, line.Column);
    }

    /// <summary>
    /// What a value says under the pointer. Its path and kind, the file it came from if it isn't this one, and the
    /// comment written over it, which is how every data file of Fighter2D says what its keys mean.
    /// </summary>
    private string ValueTooltip(string path, IRuntimeValue value, SourceLocation? at)
    {
        string said = $"{path}, {Values.TypeName(value)}";
        if (at is not { Span.IsKnown: true } known)
            return said;

        bool here = known.File is null || (document.Path is { } open && string.Equals(Path.GetFullPath(known.File), Path.GetFullPath(open), PlatformPaths.Comparison));
        if (!here)
            return $"{said}, from {Path.GetFileName(known.File)} line {known.Span.Line}{(known.Doc is { } elsewhere ? $"\n{elsewhere}" : string.Empty)}";

        // What a // @doc says about it, or failing that the plain comment over it
        if (known.Doc is { } doc)
            return $"{said}\n{doc}";

        string comment = CommentAbove(known.Span.Line - 1);
        return comment.Length > 0 ? $"{said}\n{comment}" : said;
    }

    /// <summary>The lines of // comment right over a line of the code on screen, joined, nothing if there are none.</summary>
    private string CommentAbove(int line)
    {
        var lines = new List<string>();
        for (int at = line - 1; at >= 0 && lines.Count < 8; at--)
        {
            string text = code.Document.LineAt(at).Trim();
            if (!text.StartsWith("//", StringComparison.Ordinal))
                break;
            lines.Insert(0, text[2..].Trim());
        }

        return string.Join(' ', lines);
    }

    private void ShowValueMenu(string path, IRuntimeValue value)
    {
        var shown = new Menu("value");
        shown.Add(context.Strings.Said("values.copy_path"), () => UIClipboard.Copy(path), icon: "copy");
        shown.Add(context.Strings.Said("values.copy_value"), () => UIClipboard.Copy(HexEvaluation.Describe(value)), icon: "copy");
        shown.Add(context.Strings.Said("values.in_console"), () => AskConsole(path), icon: "terminal");
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>The icon of a value, by what it is.</summary>
    private static string ValueIcon(IRuntimeValue value) => value switch
    {
        ObjectValue => "symbol-object",
        ListValue => "symbol-array",
        StringValue => "symbol-string",
        NumberValue => "symbol-number",
        BooleanValue => "symbol-boolean",
        NullValue => "symbol-null",
        Vector4Value => "symbol-color",
        Vector2Value or Vector3Value => "symbol-ruler",
        _ when Values.IsFunction(value) => "symbol-method",
        _ => "symbol-misc"
    };

    /// <summary>A value in a few words, after its name in the tree.</summary>
    private static string Summarise(IRuntimeValue value) => value switch
    {
        ObjectValue obj => $"{{ {obj.Properties.Count} }}",
        ListValue list => $"[ {list.Count} ]",
        StringValue text => $"\"{HexEvaluation.Shorten(text.Value ?? string.Empty, 48)}\"",
        FunctionValue function => $"func({string.Join(", ", function.Parameters)})",
        AnonymousFunctionValue function => $"func({string.Join(", ", function.Parameters)})",
        _ => HexEvaluation.Shorten(HexEvaluation.Describe(value), 60)
    };

    /// <summary>
    /// Where something of the file is in its code, by its path, through the outline of the file. Each step of the path is
    /// the first key of that name deeper in than the one before it, an index into a list stays on the list.
    /// </summary>
    internal (int Line, int Column)? LineOf(string path)
    {
        List<OutlineEntry> entries = window.Outline();
        int at = -1, depth = -1;
        (int, int)? found = null;

        foreach (string step in PathSteps(path))
        {
            int next = -1;
            for (int i = at + 1; i < entries.Count; i++)
            {
                // Out of what the step before was in, no further
                if (at >= 0 && entries[i].Depth <= depth)
                    break;

                if (entries[i].Name == step && entries[i].Depth > depth)
                {
                    next = i;
                    break;
                }
            }

            if (next < 0)
                break;

            at = next;
            depth = entries[next].Depth;
            found = (entries[next].Line, entries[next].Column);
        }

        return found;
    }

    /// <summary>The names in a path, "moves.jab.phases[0].cancel" is moves, jab, phases and cancel.</summary>
    private static IEnumerable<string> PathSteps(string path)
    {
        var step = new StringBuilder();
        bool inIndex = false;
        foreach (char character in path)
        {
            if (character == '[') inIndex = true;
            else if (character == ']') inIndex = false;
            else if (!inIndex && character == '.')
            {
                if (step.Length > 0) yield return step.ToString();
                step.Clear();
            }
            else if (!inIndex)
            {
                step.Append(character);
            }
        }

        if (step.Length > 0)
            yield return step.ToString();
    }

    /// <summary>Another document came on screen, what F5 and the console had of the last one is let go of.</summary>
    internal void Forget()
    {
        runOutput.Clear();
        consoleRuntime = null;
    }

    /// <summary>The table made again the next time it's up, the view having changed.</summary>
    internal void RefreshTable() => tabledFrom = null;
}
