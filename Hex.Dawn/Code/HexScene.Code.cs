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

    // What running a file of code last came to, put in the output
    private readonly List<string> runOutput = [];

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
        editor.FocusLine = true;
        editor.WordWrap = settings.WrapCode;
        editor.CaretGlide = Motion ? CARET_GLIDE : 0.0f;
        editor.Font = codeFont;
        editor.SignatureSource = (name, argument) => TerminalSignature(prepared, name, argument) ?? SignatureOf(editor, name, argument);
        PrepareBreakpoints(prepared);
        ShowBackdrop(editor);
        editor.Color = new Vector4(0.055f, 0.06f, 0.08f, 1.0f);
        editor.OnChanged = () => CodeChanged(prepared);
        editor.CompletionSource = Completions;
        editor.OnChecked = _ => CodeChecked(prepared);
        editor.HoverSource = HoverHint;
        editor.OnSwatchPressed = PickSwatch;
        editor.ArtPath = name => ArtFileOf(prepared, name);
        editor.ArtSkin = () => stage.Skin;
        editor.OnContextMenu = point => ShowCodeMenu(prepared, point);
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
        localisationFound[checkedDocument] = HexSearch.LocalisationDiagnostics(checkedDocument.Editor.Text);
        ShowDiagnostics(checkedDocument);

        if (checkedDocument == document)
        {
            inspectorDirty |= checkedDocument.IsText;
            if (checkedDocument.Editor.SyntaxError is { } error)
                Say($"line {error.Line + 1}: {error.Message}", error: true);
        }
    }

    // The run the lints were last worked out of, and what they said
    private HexValues? linted;
    private List<UICodeDiagnostic> lints = [];

    // What the localisation check last found in each document, kept to go with what running it found
    private readonly Dictionary<HexDocument, IReadOnlyList<UICodeDiagnostic>> localisationFound = [];

    /// <summary>
    /// Helper method to put what is wrong with a document under its code. The texts it names that the game hasn't got,
    /// and where running it went wrong, if that was in this file.
    /// </summary>
    private void ShowDiagnostics(HexDocument of)
    {
        var found = new List<UICodeDiagnostic>(localisationFound.GetValueOrDefault(of) ?? []);

        // What the values of the last run of it say, worked out once a run
        if (values is { Error: null } clean && valuesOf == of)
        {
            if (!ReferenceEquals(linted, clean))
            {
                linted = clean;
                lints = Lint(clean, of);
            }

            found.AddRange(lints);
        }

        if (values is { Error: { } error, ErrorAt.Span.IsKnown: true } ran && valuesOf == of
            && (ran.ErrorAt.File is null || (of.Path is { } path && string.Equals(Path.GetFullPath(ran.ErrorAt.File), Path.GetFullPath(path), PlatformPaths.Comparison))))
        {
            found.Add(new UICodeDiagnostic(ran.ErrorAt.Span.Line - 1, ran.ErrorAt.Span.Column - 1, Math.Max(1, ran.ErrorAt.Span.Length), error));
        }

        found.AddRange(ExtensionProblemsOf(of.Path));
        found.AddRange(TerminalProblemsOf(of));
        foreach (var source in diagnosticSources)
            found.AddRange(source(of) ?? []);
        of.Editor.Diagnostics = found;
        if (of == document)
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
        RaiseExtensions("on_open", new StringValue(Path.GetFullPath(path)));

        // A file an extension has a view for by its #define comes up on it
        OpenOnExtensionView(opened);

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

    /// <summary>
    /// F5 on a file of code. It is run the way a game loads it, includes looked for next to it, and what it declares is
    /// listed with what each comes to, or what went wrong. Nothing a game would hand it (a compositor) is there.
    /// </summary>
    private void RunCode()
    {
        runOutput.Clear();

        var runtime = new HIDLRuntime
        {
            BaseDirectory = FolderOf(document),
            Output = line => runOutput.Add($"print: {line}")
        };

        // Run here and now, somebody asked for it, but not for ever
        using var cancel = new CancellationTokenSource(1000);
        runtime.Interpreter.Cancellation = cancel.Token;
        var (success, result) = runtime.Evaluate(code.Text);
        runtime.Interpreter.Cancellation = CancellationToken.None;

        if (!success)
        {
            runOutput.Add($"didn't run, {result}");
            Say($"{document.Name} didn't run, {result}", error: true);
        }
        else
        {
            foreach (var (name, value) in runtime.UserScope.Variables)
                runOutput.Add($"{name} = {Shorten(Describe(value), 240)}");

            Say($"{document.Name} runs, {runtime.UserScope.Variables.Count} things declared, see the output");
        }

        // What it printed and what it declared go in the output, which comes up, or in the console of the terminal page
        // when the settings say so (run_to_terminal), where it reads like anything else a program printed
        if (settings.RunToTerminal)
        {
            ConsoleLine($"{document.Name}, run", DimColor);
            foreach (string line in runOutput)
                ConsoleLine(line, line.StartsWith("didn't run", StringComparison.Ordinal) ? ErrorColor : null);
        }
        else
        {
            foreach (string line in runOutput)
                Note(line);
            ShowPanel(OUTPUT_PAGE);
        }

        // And the console works in what it left behind
        consoleRuntime = runtime;
        inspectorDirty = true;
    }

    private static string Describe(IRuntimeValue value) => HexEvaluation.Describe(value);

    private static string Shorten(string text, int most)
    {
        string flat = text.Replace("\r", string.Empty).Replace('\n', ' ');
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
        return flat.Length <= most ? flat : flat[..most] + "...";
    }

    /// <summary>
    /// The commands of code in any file, the Selection menu (the code editor answers its keys itself), its zoom, going
    /// about it, checking it, running it and putting it back into its layout. These go while typing, the editor of code
    /// is where they are wanted most.
    /// </summary>
    private void AddCodeCommands()
    {
        Func<bool> inCode = () => !LayoutTools;

        commands.Add("Edit/comment", "Toggle Line Comment", () => code.Document.ToggleComment(), "Ctrl+/", "comment").IsVisible = inCode;

        commands.ShowMenuWhen("Selection", inCode);
        commands.Add("Selection/select", "Select All", () => code.Document.SelectAll(), "Ctrl+A");
        commands.Add("Selection/select", "Select Line", SelectCaretLine);
        commands.Add("Selection/select", "Select Word", () => code.Document.SelectWordAt(code.Document.Caret));
        commands.Add("Selection/lines", "Move Line Up", () => code.Document.MoveLines(-1), "Alt+Up", "arrow-up");
        commands.Add("Selection/lines", "Move Line Down", () => code.Document.MoveLines(1), "Alt+Down", "arrow-down");
        commands.Add("Selection/lines", "Duplicate Line", () => code.Document.Duplicate(), "Ctrl+Shift+D", "copy");
        commands.Add("Selection/carets", "Add Next Occurrence", () => code.Document.AddNextOccurrence(), "Ctrl+D", "selection");
        commands.Add("Selection/carets", "Add Cursor Above", () => code.Document.AddCaretVertically(-1), "Ctrl+Alt+Up");
        commands.Add("Selection/carets", "Add Cursor Below", () => code.Document.AddCaretVertically(1), "Ctrl+Alt+Down");
        commands.Add("Selection/carets", "Add Cursors to Line Ends", () => code.Document.CaretsAtLineEnds(), "Ctrl+Shift+L");
        commands.Add("Selection/carets", "Single Cursor", () => code.Document.CollapseCarets(), "Esc").IsEnabled = () => code.Document.HasExtraCarets;
        Menu folding = commands.AddMenu("Selection/folding", "Folding", "fold");
        folding.Add("Fold", () => code.FoldAtCaret(), "Ctrl+Shift+[", "fold-up");
        folding.Add("Unfold", () => code.UnfoldAtCaret(), "Ctrl+Shift+]", "fold-down");
        folding.AddSeparator();
        folding.Add("Fold All", () => code.FoldAll(1));
        folding.Add("Fold Level 2", () => code.FoldAll(2));
        folding.Add("Fold Level 3", () => code.FoldAll(3));
        folding.Add("Unfold All", () => code.UnfoldAll());

        // Their keys are bound with the designer's, which zooms the canvas with the same ones
        commands.Add("View/zoom", "Zoom In", () => ZoomCode(CODE_ZOOM_STEP), "Ctrl+=", "zoom-in").IsVisible = inCode;
        commands.Add("View/zoom", "Zoom Out", () => ZoomCode(1.0f / CODE_ZOOM_STEP), "Ctrl+-", "zoom-out").IsVisible = inCode;
        commands.Add("View/zoom", "Reset Zoom", () => ZoomCode(0.0f), "Ctrl+0").IsVisible = inCode;

        commands.Add("Go/symbols", "Go to Definition", GoToDefinition, "F12", "references", HexKeyUse.BoundWhileTyping).IsVisible = inCode;
        commands.Add("Go/symbols", "Find All References", FindReferences, "Shift+F12", "references", HexKeyUse.BoundWhileTyping).IsVisible = inCode;
        commands.Add("Go/problems", "Next Problem", () => StepProblem(1), icon: "arrow-down");
        commands.Add("Go/problems", "Previous Problem", () => StepProblem(-1), icon: "arrow-up");

        // F2 renames, a name in the code, or what is selected in the designer by its name in the properties
        commands.Bind(Key.F2, () => { if (compositor.Focus is CodeEditor) RenameSymbol(); else if (LayoutTools) RenameSelected(); }, whileTyping: true);

        // A file of code is run with F5 (see RunOrPlay), and with control a function of it is called tick after tick
        commands.Add("Run/run", "Run File", RunCode, "F5", "play").IsVisible = () => !LayoutTools && !IsCSharp(document.Path);
        commands.Add("Run/run", "Run Behaviour...", OpenRunner, "Ctrl+F5", "debug-alt").IsVisible = inCode;
        commands.Bind(Key.F5, () => { if (!LayoutTools) OpenRunner(); }, control: true, whileTyping: true);

        commands.Add("Run/check", "Check All Files", CheckAllFiles, "F8", "check-all", HexKeyUse.BoundWhileTyping);
        commands.Add("Run/check", "Trigger Completion", () => { code.Focus(); code.Complete(); }, "Ctrl+Space").IsVisible = inCode;

        commands.Add("Run/apply", "Apply Code to Layout", ApplyCode, "Ctrl+Enter", use: HexKeyUse.BoundWhileTyping).IsEnabled = () => document.CodeEdited;
        commands.Add("Run/apply", "Discard Code Changes", RevertCode, icon: "discard").IsEnabled = () => document.CodeEdited;
        commands.Add("Run/apply", "Convert to Layout...", MakeLayout, icon: "layout").IsEnabled = () => document.IsText && IsLayoutCode(code.Text);
    }
}
