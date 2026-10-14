using System.Numerics;
using System.Text;

using Horizon.Content;
using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Hex as the editor of every .hor there is and not only of layouts. A file that doesn't build a layout (a data file, a
// language, a skin, a move set) opens as code, the code of a layout can be edited by hand and put back into it, and
// there is finding, going to a line, opening any file of the workspace by a few letters of its name, finding across
// all of them, checking all of them for what doesn't parse, and running a file to see what it comes to.
internal sealed partial class HexScene
{
    // The editor of the document on screen, where its code is edited and the code of a layout is shown
    private CodeEditor code => document.Editor;

    // Set while the program puts code into an editor, so that isn't taken for somebody typing
    private bool settingCode;

    // What the list on the left shows instead of the layout or the files, the places something was found (null for nothing)
    private List<HexResult>? results;
    private string resultsTitle = string.Empty;

    // What was last looked for, to look for again
    private string lastFind = string.Empty;

    // What running a file of code last came to, shown in the properties
    private readonly List<string> runOutput = [];

    // The folders never looked in for files of a workspace, what gets built, git's own and old builds kept on purpose
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "Build History" };

    private readonly record struct HexResult(string Path, int Line, int Column, string Text);

    // The parts of the editor's own layout that only mean something for a layout, hidden while a file of code is open
    private const string LINK_TEMPLATE = "link.hor";

    private static readonly string[] LayoutOnlyParts = ["toolbox", "tree_tools", "layers_section"];

    /// <summary>Whether a file's code builds a layout, which it does by asking the compositor for its components.</summary>
    private static bool IsLayoutCode(string source) => source.Contains("compositor.", StringComparison.Ordinal);

    /// <summary>Helper method to set an editor up the way the editor's code is shown, once, when its document is made.</summary>
    private void PrepareEditor(HexDocument prepared)
    {
        CodeEditor editor = prepared.Editor;
        editor.TextScale = EDITOR_TEXT * settings.CodeZoom;
        editor.OnZoom = notches => ZoomCode(notches > 0.0f ? CODE_ZOOM_STEP : 1.0f / CODE_ZOOM_STEP);
        editor.Padding = new UIEdges(4);
        editor.Color = new Vector4(0.055f, 0.06f, 0.08f, 1.0f);
        editor.OnChanged = () => CodeChanged(prepared);
        editor.CompletionSource = Completions;
        editor.OnChecked = _ => CodeChecked(prepared);
        editor.HoverSource = HoverHint;
        editor.OnSwatchPressed = PickSwatch;
        editor.OnContextMenu = point => ShowCodeMenu(prepared, point);
    }

    private void CodeChanged(HexDocument changed)
    {
        if (settingCode)
            return;

        if (changed.IsText)
        {
            codeDirty = true;
            return;
        }

        // The code of a layout typed into, it goes back into the layout when it is applied
        if (!changed.CodeEdited)
        {
            changed.CodeEdited = true;
            tabsDirty = true;
            Say("the code was changed by hand, Ctrl+Enter puts it into the layout");
        }
    }

    /// <summary>
    /// Helper method to put code into an editor that the editor's user didn't type, the code a layout is written as, or
    /// a file that was opened. As a step that can be undone unless it is the first thing the editor ever had.
    /// </summary>
    private void SetCode(HexDocument into, string text, bool fresh)
    {
        settingCode = true;
        try
        {
            if (fresh)
                into.Editor.Text = text;
            else if (into.Editor.Text != text)
                into.Editor.Document.Replace(text);
        }
        finally
        {
            settingCode = false;
        }
    }

    /// <summary>After the code of a document was parsed, what it says about the texts of the game it names by key.</summary>
    private void CodeChecked(HexDocument checkedDocument)
    {
        checkedDocument.Editor.Diagnostics = LocalisationDiagnostics(checkedDocument.Editor.Text);

        if (checkedDocument == document)
        {
            inspectorDirty |= checkedDocument.IsText;
            if (checkedDocument.Editor.SyntaxError is { } error)
                Say($"line {error.Line + 1}: {error.Message}", error: true);
        }
    }

    /// <summary>
    /// Helper method to find the texts a piece of code names by key ("@main_menu.quit") that the language of its game
    /// hasn't got, with no languages loaded there is nothing to say.
    /// </summary>
    private static IReadOnlyList<UICodeDiagnostic> LocalisationDiagnostics(string text)
    {
        if (Localisation.Count == 0 || !text.Contains("\"@", StringComparison.Ordinal))
            return [];

        var found = new List<UICodeDiagnostic>();
        foreach (Token token in Lexer.Tokenize(text, lenient: true))
        {
            if (token.Type != TokenType.TextLiteral || !Localisation.IsKey(token.Value))
                continue;

            if (!Localisation.Has(token.Value[1..]))
                found.Add(new UICodeDiagnostic(token.Line - 1, token.Col - 1, token.Length, $"{Localisation.Language} has no text for {token.Value[1..]}", Warning: true));
        }

        return found;
    }

    /* Text documents */

    /// <summary>Opens a file of code that isn't a layout, as the code it is.</summary>
    private bool OpenText(string path, string source)
    {
        // The empty layout the editor starts with is put away, nobody wants it next to a file
        if (document is { Path: null, IsText: false } && !document.Module.Root.Children.Any() && !document.Modified)
        {
            stage.RemoveModule(document.Module);
            documents.Remove(document);
        }

        var opened = new HexDocument(Path.GetFileNameWithoutExtension(path), stage.CreateModule())
        {
            IsText = true,
            Path = path,
            FallbackDirectory = browseDirectory,
            CrLf = source.Contains("\r\n", StringComparison.Ordinal),
            SavedCrLf = source.Contains("\r\n", StringComparison.Ordinal)
        };

        opened.Module.Interactive = false;
        PrepareEditor(opened);

        // Only HIDL is coloured and parsed as HIDL, anything else is text
        bool hidl = Path.GetExtension(path).Equals(".hor", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(HexWorkspace.EXTENSION, StringComparison.OrdinalIgnoreCase);
        opened.Editor.Highlight = hidl;
        opened.Editor.CheckSyntax = hidl;
        SetCode(opened, source, fresh: true);
        opened.SavedCode = opened.Editor.Text;

        documents.Add(opened);
        UseLanguagesNear(path);
        Show(opened);
        Remember(path);

        Console.WriteLine($"[Hex] Opened '{path}' as code: {opened.Editor.Document.LineCount} lines.");
        Say($"opened {opened.Name}, {opened.Editor.Document.LineCount} lines of code");
        return true;
    }

    /// <summary>Opens a file and puts the caret somewhere in it, for whatever lists places in files.</summary>
    private void OpenAt(string path, int line, int column)
    {
        string full = Path.GetFullPath(path);
        HexDocument? open = documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path) == full);

        if (open is null)
        {
            if (!Open(path) && !documents.Any(other => other.Path is not null && Path.GetFullPath(other.Path) == full))
                return;
        }
        else
        {
            Show(open);
        }

        code.GoTo(line, column);
        code.Focus();
    }

    /* The code of a layout, put back into it */

    /// <summary>Builds the layout again out of its code as it was edited by hand. What doesn't build is said and left as it is.</summary>
    private void ApplyCode()
    {
        if (document.IsText || !document.CodeEdited)
            return;

        var (success, message) = document.Load(code.Text, document.Path, keepHistory: true);
        if (!success)
        {
            Say($"the code didn't build, {message}", error: true);
            return;
        }

        document.CodeEdited = false;
        document.Record();
        MarkSelection();
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;
        Say(message.Length > 0 ? message : $"the code is in the layout, {document.Walk().Count()} components");
    }

    /// <summary>Throws away what was typed into the code of a layout, for the code the layout is written as.</summary>
    private void RevertCode()
    {
        if (!document.CodeEdited)
            return;

        document.CodeEdited = false;
        SetCode(document, document.GenerateCode(), fresh: false);
        tabsDirty = codeDirty = true;
        Say("the code is what the layout says again");
    }

    /* What the properties show for a file of code, what is wrong with it and what running it came to */

    private void RebuildCodeInspector()
    {
        foreach (var child in inspector.Children.ToArray())
            inspector.Remove(child);

        Label Line(string text, Vector4? color = null)
        {
            Label line = Hint(inspector, Plain(text));
            line.Color = color ?? DimColor;
            line.Wrap = true;
            line.Size = new Vector2(MathF.Max(120, inspectorScroll.Size.X - 30), 0);
            return line;
        }

        Button Link(string text, Action pressed)
        {
            var link = layout.Instantiate(LINK_TEMPLATE, inspector).Get<Button>("link");
            link.Label = Plain(text);
            link.OnPressed = pressed;
            return link;
        }

        Line(document.Path ?? document.Name, null);
        Line($"{code.Document.LineCount} lines, {(document.CrLf ? "CRLF" : "LF")}");

        if (code.SyntaxError is { } error)
        {
            Link($"line {error.Line + 1}: {error.Message}", () =>
            {
                code.GoTo(error.Line, error.Column);
                code.Focus();
            }).Icon = "error";
        }
        else
        {
            Line("parses", new Vector4(0.45f, 0.85f, 0.5f, 1.0f));
        }

        foreach (UICodeDiagnostic warning in code.Diagnostics)
        {
            Link($"line {warning.Line + 1}: {warning.Message}", () =>
            {
                code.GoTo(warning.Line, warning.Column);
                code.Focus();
            }).Icon = "warning";
        }

        if (runOutput.Count > 0)
        {
            Line(string.Empty);
            Line("WHAT RUNNING IT CAME TO (F5)", new Vector4(0.46f, 0.52f, 0.64f, 1.0f));
            foreach (string said in runOutput)
                Line(said, null);
        }
        else
        {
            Line(string.Empty);
            Line("F5 runs it and lists what it declares, Ctrl+F finds, Ctrl+G goes to a line, Ctrl+P opens any file, Ctrl+Shift+F finds in all of them");
        }
    }

    /// <summary>
    /// F5 on a file of code. It is run the way a game loads it, includes looked for next to it, and what it declares is
    /// listed with what each comes to, or what went wrong. Nothing a game would hand it (a compositor) is there.
    /// </summary>
    private void RunCode()
    {
        runOutput.Clear();

        var runtime = new HIDLRuntime
        {
            BaseDirectory = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : browseDirectory,
            Output = line => runOutput.Add($"print: {line}")
        };

        var (success, result) = runtime.Evaluate(code.Text);
        if (!success)
        {
            runOutput.Add($"didn't run, {result}");
            Say($"{document.Name} didn't run, {result}", error: true);
        }
        else
        {
            foreach (var (name, value) in runtime.UserScope.Variables)
                runOutput.Add($"{name} = {Shorten(Describe(value), 240)}");

            Say($"{document.Name} runs, {runtime.UserScope.Variables.Count} things declared, listed on the right");
        }

        inspectorDirty = true;
    }

    private static string Describe(IRuntimeValue value)
    {
        try
        {
            return HIDLWriter.CanWrite(value) ? HIDLWriter.Write(value) : value.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string Shorten(string text, int most)
    {
        string flat = text.Replace("\r", string.Empty).Replace('\n', ' ');
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= most ? flat : flat[..most] + "...";
    }
}
