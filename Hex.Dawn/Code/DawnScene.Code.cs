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
using Horizon.UI.Scripting;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// Hex as the editor of every .hor there is and not only of layouts. A file that doesn't build a layout (a data file, a
// language, a skin, a move set) opens as code, the code of a layout can be edited by hand and put back into it, and
// there is finding, going to a line, opening any file of the workspace by a few letters of its name, finding across
// all of them, checking all of them for what doesn't parse, and running a file to see what it comes to.
internal sealed partial class DawnScene
{
    // The editor of the document on screen, where its code is edited and the code of a layout is shown
    private CodeEditor code => document.Editor;

    // Set while the program puts code into an editor, so that isn't taken for somebody typing
    private bool settingCode;

    // What the list on the left shows instead of the layout or the files, the places something was found (null for nothing)
    private List<HexResult>? results;
    private string resultsTitle = string.Empty;



    // The parts of the editor's own layout that only mean something for a layout, hidden while a file of code is open
    private const string LINK_TEMPLATE = "link.hor";

    private static readonly string[] LayoutOnlyParts = ["toolbox", "tree_tools", "layers_section"];

    /// <summary>Whether a file's code builds a layout, which it does by asking the compositor for its components.</summary>
    private static bool IsLayoutCode(string source) => source.Contains("compositor.", StringComparison.Ordinal);

    // How big code is written at a zoom of 1, a monospaced font a little bigger than the UI's for the same size on screen.
    // At the UI's own it came out a point smaller than the words round it
    internal const float CODE_TEXT = 0.155f;

    /// <summary>Helper method to set an editor up the way the editor's code is shown, once, when its document is made.</summary>
    private void PrepareEditor(HexDocument prepared)
    {
        CodeEditor editor = prepared.Editor;
        editor.TextScale = CODE_TEXT * settings.CodeZoom;
        editor.OnZoom = notches => ZoomCode(notches > 0.0f ? CODE_ZOOM_STEP : 1.0f / CODE_ZOOM_STEP);
        editor.Padding = new UIEdges(4);
        editor.WordWrap = settings.WrapCode;
        editor.CaretGlide = CaretGlide;
        editor.Font = codeEditing.codeFont;
        editor.SignatureSource = (name, argument) => codeEditing.Signature(prepared, editor, name, argument);
        ShowBackdrop(editor);
        // The code on the middle card's own colour, one surface with the tabs over it and the terminal under it. It was a
        // charcoal of its own written in here, a third colour in the one card
        editor.Color = compositor.Skin is { } skin ? skin.PanelColor with { W = 1.0f } : new Vector4(0.08f, 0.14f, 0.2f, 1.0f);
        editor.OnChanged = () => CodeChanged(prepared);
        editor.OnTab = () => extensions.Tab(prepared);
        editor.CompletionSource = codeEditing.Completions;
        editor.OnChecked = _ => CodeChecked(prepared);
        editor.HoverSource = codeEditing.HoverHint;
        editor.OnSwatchPressed = codeEditing.PickSwatch;
        editor.ArtPath = name => ArtFileOf(prepared, name);
        editor.ArtSkin = () => stage.Skin;
        editor.OnContextMenu = point => codeEditing.ShowCodeMenu(prepared, point);
    }

    /// <summary>
    /// Where a picture the code names is, looked for next to the file and in every folder over it (a layout names its art
    /// from the game's folder, which is somewhere up there), then in the folders of the workspace, then from where Hex
    /// was started. Null for nowhere, and the editor draws nothing for it.
    /// </summary>
    private string? ArtFileOf(HexDocument of, string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        // Eight folders up is past any game's folder there is, and stops a name being looked for all the way to the drive
        string? from = of.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : of.FallbackDirectory;
        int up = 0;
        for (string? at = from; !string.IsNullOrEmpty(at) && up < 8; at = Path.GetDirectoryName(at), up++)
        {
            string here = Path.Combine(at, name);
            if (File.Exists(here))
                return here;
        }

        foreach (string root in WorkspaceRoots())
        {
            string here = Path.Combine(root, name);
            if (File.Exists(here))
                return here;
        }

        return File.Exists(name) ? Path.GetFullPath(name) : null;
    }

    private void CodeChanged(HexDocument changed)
    {
        if (settingCode)
            return;

        if (changed.IsText)
        {
            codeDirty = true;
            documentEdited?.Invoke(changed);

            // What's wrong moves along with the lines typed in above it (CodeEditor), the Problems panel's lines with it
            if (changed == document)
                problemsDirty = true;
            return;
        }

        // The code of a layout typed into, it goes back into the layout when it is applied
        if (!changed.CodeEdited)
        {
            changed.CodeEdited = true;
            tabsDirty = true;
            Say(Said("code.changed_by_hand"));
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

        // A file's text changed by Hex rather than typed (what wasn't saved brought back, the file read again after it
        // changed on the disk) is a change all the same to whoever keeps a copy of it. Its language server was never
        // told, and said what was wrong with the text on the disk under the text on the screen
        if (!fresh && into.IsText)
            documentEdited?.Invoke(into);
    }

    /// <summary>After the code of a document was parsed, what it says about the texts of the game it names by key.</summary>
    private void CodeChecked(HexDocument checkedDocument)
    {
        localisationFound[checkedDocument] = HexSearch.LocalisationDiagnostics(checkedDocument.Editor.Text);
        ShowDiagnostics(checkedDocument);

        if (checkedDocument == document)
        {
            inspectorDirty |= checkedDocument.IsText;
            if (checkedDocument.Editor.SyntaxError is { } error)
                Say(Said("code.syntax_error", ("line", (error.Line + 1).ToString()), ("message", error.Message)), error: true);
        }
    }


    // What a diagnostic says without who said it, in brackets at its end ("(dmd)", "(dscanner.style.x)")
    private static string WithoutSource(string message) =>
        message.EndsWith(')') && message.LastIndexOf(" (", StringComparison.Ordinal) is var at and > 0 && message.IndexOf(' ', at + 2) < 0 ? message[..at] : message;

    // What the localisation check last found in each document, kept to go with what running it found
    private readonly Dictionary<HexDocument, IReadOnlyList<UICodeDiagnostic>> localisationFound = [];

    /// <summary>
    /// Helper method to put what is wrong with a document under its code. The texts it names that the game hasn't got,
    /// and where running it went wrong, if that was in this file.
    /// </summary>
    private void ShowDiagnostics(HexDocument of)
    {
        var found = new List<UICodeDiagnostic>(localisationFound.GetValueOrDefault(of) ?? []);

        // What the run of it says (HexRunning), what the extensions found, the terminal's files and the language servers.
        // The same thing said twice in the same place is said once, the compiler's check and the build that ran it both
        // said a string doesn't go in an int, and the Problems panel listed it twice
        var seen = new HashSet<(int, int, string)>();
        foreach (var source in diagnosticSources)
        {
            foreach (UICodeDiagnostic diagnostic in source(of) ?? [])
            {
                if (seen.Add((diagnostic.Line, diagnostic.Column, WithoutSource(diagnostic.Message))))
                    found.Add(diagnostic);
            }
        }
        of.Editor.Diagnostics = found;

        // Every open file's are in the Problems panel, not only the one on screen
        problemsDirty = true;
    }

    /* Text documents */

    /// <summary>Opens a file of code that isn't a layout, as the code it is.</summary>
    /// <param name="show">Whether it comes to the front, an extension editing a file opens it behind the one on screen.</param>
    private bool OpenText(string path, string source, bool show = true)
    {
        // The empty layout the editor starts with is put away, nobody wants it next to a file
        if (show && document is { Path: null, IsText: false } && !document.Module.Root.Children.Any() && !document.Modified)
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
            SavedCrLf = source.Contains("\r\n", StringComparison.Ordinal),
            DiskText = source
        };

        opened.Module.Interactive = false;
        PrepareEditor(opened);

        // Coloured by the grammar of whatever language the extension says (HIDL's own, VS Code's for the rest, plain
        // text when there is none), and only HIDL is parsed for its errors, until the rest come through a language server
        opened.Editor.Highlighter = CodeLanguages.ForFile(path);
        opened.Editor.CheckSyntax = ReferenceEquals(opened.Editor.Highlighter, CodeLanguages.Hidl);
        SetCode(opened, source, fresh: true);
        opened.SavedCode = opened.Editor.Text;
        documentOpened?.Invoke(opened);

        documents.Add(opened);
        if (!show)
        {
            WatchOpenFiles();
            tabsDirty = true;
            Console.WriteLine($"[Hex] Opened '{path}' as code behind the file on screen.");
            return true;
        }

        UseLanguagesNear(path);
        Show(opened);
        Remember(path);
        extensions.Raise("on_open", new StringValue(Path.GetFullPath(path)));

        // A file an extension has a view for by its #define comes up on it
        OpenOnExtensionView(opened);

        Console.WriteLine($"[Hex] Opened '{path}' as code: {opened.Editor.Document.LineCount} lines.");
        int lines = opened.Editor.Document.LineCount;
        Say(Said("code.opened", ("name", opened.Path is { } openedPath ? Path.GetFileName(openedPath) : opened.Name), ("lines", HexText.Count("line", lines))));
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
            Say(Said("code.did_not_build", ("message", message)), error: true);
            return;
        }

        document.CodeEdited = false;
        document.Record();
        designer.MarkSelection();
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;
        Say(message.Length > 0 ? message : Said("code.applied", ("count", HexText.Count("component", document.Walk().Count()))));
    }

    /// <summary>Throws away what was typed into the code of a layout, for the code the layout is written as.</summary>
    private void RevertCode()
    {
        if (!document.CodeEdited)
            return;

        document.CodeEdited = false;
        SetCode(document, document.GenerateCode(), fresh: false);
        tabsDirty = codeDirty = true;
        Say(Said("code.reverted"));
    }

    private static string Describe(IRuntimeValue value) => HexEvaluation.Describe(value);

    /// <summary>
    /// The commands of code in any file, the Selection menu (the code editor answers its keys itself), its zoom, going
    /// about it, checking it, running it and putting it back into its layout. These go while typing, the editor of code
    /// is where they are wanted most.
    /// </summary>
    private void AddCodeCommands()
    {
        Func<bool> inCode = () => !LayoutTools;

        commands.Add("Edit/comment", Said("code.toggle_comment"), () => code.Document.ToggleComment(), "Ctrl+/", "comment").IsVisible = inCode;

        commands.ShowMenuWhen("Selection", inCode);
        commands.Add("Selection/select", Said("designer_selection.select_all"), () => code.Document.SelectAll(), "Ctrl+A");
        commands.Add("Selection/select", Said("code.select_line"), SelectCaretLine);
        commands.Add("Selection/select", Said("code.select_word"), () => code.Document.SelectWordAt(code.Document.Caret));
        commands.Add("Selection/lines", Said("code.move_line_up"), () => code.Document.MoveLines(-1), "Alt+Up", "arrow-up");
        commands.Add("Selection/lines", Said("code.move_line_down"), () => code.Document.MoveLines(1), "Alt+Down", "arrow-down");
        commands.Add("Selection/lines", Said("code.duplicate_line"), () => code.Document.Duplicate(), "Ctrl+Shift+D", "copy");
        commands.Add("Selection/carets", Said("code.add_next_occurrence"), () => code.Document.AddNextOccurrence(), "Ctrl+D", "selection");
        commands.Add("Selection/carets", Said("code.add_cursor_above"), () => code.Document.AddCaretVertically(-1), "Ctrl+Alt+Up");
        commands.Add("Selection/carets", Said("code.add_cursor_below"), () => code.Document.AddCaretVertically(1), "Ctrl+Alt+Down");
        commands.Add("Selection/carets", Said("code.add_cursors_line_ends"), () => code.Document.CaretsAtLineEnds(), "Ctrl+Shift+L");
        commands.Add("Selection/carets", Said("code.single_cursor"), () => code.Document.CollapseCarets(), "Esc").IsEnabled = () => code.Document.HasExtraCarets;
        Menu folding = commands.AddMenu("Selection/folding", Said("code.folding"), "fold");
        folding.Add(Said("code.fold"), () => code.FoldAtCaret(), "Ctrl+Shift+[", "fold-up");
        folding.Add(Said("code.unfold"), () => code.UnfoldAtCaret(), "Ctrl+Shift+]", "fold-down");
        folding.AddSeparator();
        folding.Add(Said("code.fold_all"), () => code.FoldAll(1));
        folding.Add(Said("code.fold_level_2"), () => code.FoldAll(2));
        folding.Add(Said("code.fold_level_3"), () => code.FoldAll(3));
        folding.Add(Said("code.unfold_all"), () => code.UnfoldAll());

        // Their keys are bound with the designer's, which zooms the canvas with the same ones
        commands.Add("View/zoom", Said("designer_selection.zoom_in"), () => ZoomCode(CODE_ZOOM_STEP), "Ctrl+=", "zoom-in").IsVisible = inCode;
        commands.Add("View/zoom", Said("designer_selection.zoom_out"), () => ZoomCode(1.0f / CODE_ZOOM_STEP), "Ctrl+-", "zoom-out").IsVisible = inCode;
        commands.Add("View/zoom", Said("code.reset_zoom"), () => ZoomCode(0.0f), "Ctrl+0").IsVisible = inCode;

        commands.Add("Go/symbols", Said("code.go_to_definition"), codeEditing.GoToDefinition, "F12", "references", UIKeyUse.BoundWhileTyping).IsVisible = inCode;
        commands.Add("Go/symbols", Said("code.find_all_references"), codeEditing.FindReferences, "Shift+F12", "references", UIKeyUse.BoundWhileTyping).IsVisible = inCode;
        commands.Add("Go/symbols", Said("code.rename_symbol"), codeEditing.RenameSymbol, "F2", "edit", UIKeyUse.Shown).IsVisible = inCode;

        // F8 and Shift+F8 go along what's wrong, VS Code's keys. F8 checked every HIDL file of the workspace, which is
        // Ctrl+F8 now, and somebody in a C# file who pressed F8 got told 0 files had been checked
        commands.Add("Go/problems", Said("code.next_problem"), () => StepProblem(1), "F8", "arrow-down", UIKeyUse.BoundWhileTyping);
        commands.Add("Go/problems", Said("code.previous_problem"), () => StepProblem(-1), "Shift+F8", "arrow-up", UIKeyUse.BoundWhileTyping);

        // F2 renames, a name in the code, or what is selected in the designer by its name in the properties
        commands.Bind(Key.F2, () => { if (compositor.Focus is CodeEditor) codeEditing.RenameSymbol(); else if (LayoutTools) RenameSelected(); }, whileTyping: true);

        // A file of code is run with F5 (see RunOrPlay), and with control a function of it is called tick after tick
        commands.Add("Run/run", Said("code.run_file"), running.RunCode, "F5", "play").IsVisible = () => !LayoutTools && !debugger.CanDebug(document.Path);
        commands.Add("Run/run", Said("code.run_without_debugging"), RunWithoutDebugging, "Ctrl+F5", "play").IsVisible = () => !LayoutTools && document.Path is { } path && !IsHidl(path);
        commands.Add("Run/run", Said("code.run_behaviour"), running.OpenRunner, "Ctrl+F5", "debug-alt").IsVisible = () => !LayoutTools && (document.Path is not { } path || IsHidl(path));
        commands.Bind(Key.F5, RunWithoutDebugging, control: true, whileTyping: true);

        commands.Add("Run/check", Said("code.check_all_files"), CheckAllFiles, "Ctrl+F8", "check-all", UIKeyUse.BoundWhileTyping);
        commands.Add("Run/check", Said("code.trigger_completion"), () => { code.Focus(); code.Complete(); }, "Ctrl+Space").IsVisible = inCode;

        commands.Add("Run/apply", Said("code.apply_to_layout"), ApplyCode, "Ctrl+Enter", use: UIKeyUse.BoundWhileTyping).IsEnabled = () => document.CodeEdited;
        commands.Add("Run/apply", Said("code.discard_changes"), RevertCode, icon: "discard").IsEnabled = () => document.CodeEdited;
        commands.Add("Run/apply", Said("code.convert_to_layout"), MakeLayout, icon: "layout").IsEnabled = () => document.IsText && IsLayoutCode(code.Text);
    }

    /// <summary>The outline of what is in the code editor, by the language of its file.</summary>
    private List<OutlineEntry> OutlineHere() => HexOutline.Outline(code.Text, document?.Path);
}
