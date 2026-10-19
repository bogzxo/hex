using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Horizon.Content;
using Horizon.HIDL.Lexing;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

// What the editor of code says about the word the pointer rests on (a property, a kind, a key of the game's language, a
// let, the library), its context menu, renaming a name all through a file, and picking a colour from a swatch.
internal sealed partial class HexScene
{
    /// <summary>
    /// The hint for the word at a place, markdown, null for nothing worth saying. What it is is worked out from what is
    /// round it the way the highlighter does it.
    /// </summary>
    private string? HoverHint(CodeEditor editor, UITextPosition at)
    {
        // One of the terminal's files, its settings, commands and functions said first (HexScene.TerminalFiles)
        if (documents.Find(open => open.Editor == editor) is { } of && TerminalHover(of, editor, at) is { } terminal)
            return terminal;

        string line = editor.Document.LineAt(at.Line);
        var (start, end) = editor.Document.WordAt(at);
        if (end <= start)
            return null;

        string word = line[start..end];
        string before = line[..start].TrimEnd();
        string after = line[end..].TrimStart();

        // A key of the game's language, the whole of the text "@main_menu.quit"
        int quote = line.LastIndexOf('"', Math.Max(0, start - 1));
        if (quote >= 0 && quote + 1 < line.Length && line[quote + 1] == '@')
        {
            int close = line.IndexOf('"', quote + 1);
            string key = line[(quote + 2)..(close > quote ? close : line.Length)];
            return KeyHint(key);
        }

        // The kind of a component, compositor.button
        if (before.EndsWith("compositor.", StringComparison.Ordinal))
        {
            string described = Describe(word);
            return described.Length > 0 ? $"$({KindIcon(word)}) **{word}**, a kind of component\n\n{described}" : $"**{word}**, no kind of component Hex knows";
        }

        // A property of the component being made, padding: in compositor.label({ ... })
        if (after.StartsWith(':') && ComponentBeingMade(editor.Text, editor.Document, at.Column) is { } kind)
        {
            string help = UIPropertyHelp.Describe(kind, word);
            return help.Length > 0 ? $"$(symbol-property) **{word}** of a {kind}\n\n{help}" : null;
        }

        // The language's own
        if (word == "vec")
            return "$(symbol-method) **vec**(x, y, z, w)\n\nA vector of two to four numbers. A position, a size, or a colour as red, green, blue and how solid it is, each 0 to 1.";
        if (HidlHighlighter.IsBuiltin(word))
            return LibraryHint(word);
        if (word is "let" or "const" or "func" or "if" or "else" or "while" or "for" or "in" or "return" or "break" or "continue" or "do" or "delete")
            return KeywordHint(word);

        // A directive and what it defines
        if (line.TrimStart().StartsWith('#'))
            return word == "layout"
                ? "$(layout) **#define layout**\n\nThe file is a layout, Hex opens it with its designer, which writes the whole file anew every time it is saved from there."
                : $"$(symbol-constant) **{word}**, a directive. Read before the rest of the file, `defined(\"name\")` asks after a name.";

        // A name made in this file, with what it is documented as wherever that is
        if (FindDeclaration(editor.Text, word) is { } declared)
        {
            string declaration = editor.Document.LineAt(declared.Line).Trim();
            return $"$(symbol-variable) **{word}**, made on line {declared.Line + 1}\n\n```\n{Shorten(declaration, 120)}\n```{DocHint(word)}";
        }

        // Made somewhere else, an included file or anywhere in the workspace, or a key of an object, and documented there
        if (DocOf(word) is { } doc)
        {
            string icon = after.StartsWith(':') || before.EndsWith('.') ? "symbol-field" : "symbol-variable";
            return $"$({icon}) **{word}**\n\n{Literal(doc.Doc)}{(doc.Where is { } where ? $"\n\n*{where}*" : string.Empty)}";
        }

        return null;
    }

    /// <summary>What a key of the game's language says in every language there is.</summary>
    private static string KeyHint(string key)
    {
        if (Localisation.Languages.Count == 0)
            return $"$(globe) **@{key}**\n\nNo languages are loaded, a layout near a localisation folder has its game's.";

        var text = new StringBuilder($"$(globe) **@{key}**\n");
        foreach (string language in Localisation.Languages)
        {
            string? said = Localisation.In(language, key);
            text.Append("\n- **").Append(language).Append("**  ").Append(said is null ? "*hasn't got it*" : Plain(said));
        }

        return text.ToString();
    }

    private static string LibraryHint(string word) => word switch
    {
        "print" => "$(symbol-method) **print**(value, ...)\n\nWrites what it is given to the output, F5 shows it.",
        "include" => "$(symbol-method) **include**(\"file.hor\")\n\nRuns another file, looked for next to this one, and comes to what that declares.",
        "keys" => "$(symbol-method) **keys**(object)\n\nThe keys of an object, as a list.",
        "values" => "$(symbol-method) **values**(object)\n\nThe values of an object, as a list.",
        "has" => "$(symbol-method) **has**(object or list, key or value)\n\nWhether it has it.",
        "range" => "$(symbol-method) **range**(to) or **range**(from, to)\n\nThe numbers from, up to but not with to, as a list.",
        "len" => "$(symbol-method) **len**(text or list or object)\n\nHow long it is.",
        "text" => "$(symbol-method) **text**(value)\n\nThe value written as a text.",
        "number" => "$(symbol-method) **number**(text)\n\nThe number a text is.",
        "format" => "$(symbol-method) **format**(\"{0} of {1}\", a, b)\n\nA text with the values put in where it says.",
        "typeof" => "$(symbol-method) **typeof**(value)\n\nWhat kind of value it is, number, text, list, object.",
        "error" => "$(symbol-method) **error**(\"why\")\n\nStops the script and says why.",
        "defined" => "$(symbol-method) **defined**(\"name\")\n\nWhether a directive (`#define name`) or the program defined it.",
        "definition" => "$(symbol-method) **definition**(\"name\")\n\nWhat a name was defined as, null if it wasn't.",
        "math" => "$(symbol-namespace) **math**\n\nThe library's maths, `math.sin`, `math.floor`, `math.pi` and the rest.",
        "compositor" => "$(symbol-namespace) **compositor**\n\nWhat a layout makes its components with, `compositor.label({ ... })`.",
        _ => $"$(symbol-method) **{word}**, part of the language"
    };

    private static string KeywordHint(string word) => word switch
    {
        "let" => "$(symbol-keyword) **let** name = value;\n\nMakes a name that can be set again.",
        "const" => "$(symbol-keyword) **const** name = value;\n\nMakes a name that can't be set again.",
        "func" => "$(symbol-keyword) **func** name(a, b) { ... }\n\nMakes a function.",
        "for" => "$(symbol-keyword) **for** (item in list) { ... }\n\nGoes through every item of a list.",
        _ => $"$(symbol-keyword) **{word}**, a word the language keeps to itself"
    };

    /* The editor's context menu */

    private void ShowCodeMenu(HexDocument at, Vector2 point)
    {
        CodeEditor editor = at.Editor;

        // A right click outside of what is selected puts the caret there first, the way every editor does
        UITextPosition clicked = editor.PositionAt(point);
        if (!editor.Document.HasSelection || clicked < editor.Document.SelectionStart || clicked > editor.Document.SelectionEnd)
            editor.Document.MoveTo(clicked);

        bool hasSelection = editor.Document.HasSelection;
        var shown = new Menu("code");
        shown.Add("Go to where it is declared", GoToDefinition, "F12", "references");
        shown.Add("Find everywhere it is", FindReferences, "Shift+F12", "references");
        shown.Add("Rename it...", RenameSymbol, "F2", "edit");
        shown.AddSeparator();
        shown.Add("Cut", () => { UIClipboard.Copy(editor.Document.SelectedText); editor.Document.Insert(string.Empty); }, "Ctrl+X").IsEnabled = () => hasSelection && !editor.ReadOnly;
        shown.Add("Copy", () => UIClipboard.Copy(editor.Document.SelectedText), "Ctrl+C", "copy").IsEnabled = () => hasSelection;
        shown.Add("Select all", () => editor.Document.SelectAll(), "Ctrl+A");
        shown.AddSeparator();
        shown.Add("Comment lines in and out", () => editor.Document.ToggleComment(), "Ctrl+/", "comment");
        shown.Add("Duplicate the line", () => editor.Document.Duplicate(), "Ctrl+Shift+D", "copy");
        shown.Add("The next one as well", () => editor.Document.AddNextOccurrence(), "Ctrl+D", "selection");
        shown.AddSeparator();
        shown.Add("Run the file", RunCode, "F5", "play").IsVisible = () => document.IsText;
        shown.Add("Put the code into the layout", ApplyCode, "Ctrl+Enter").IsVisible = () => !document.IsText;
        shown.Add("Command palette...", CommandPalette, "Ctrl+Shift+P", "symbol-misc");

        editor.Focus();
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>
    /// F2, a name renamed everywhere it is in the file as a word of its own, one step to undo. When other files of the
    /// workspace have it too (a key of an included file, the shared moves and a fighter's own), it asks whether to rename
    /// it in them as well, each opened behind with the rename as one step of its undo and nothing saved.
    /// </summary>
    private void RenameSymbol()
    {
        var (start, end) = code.Document.WordAt(code.Document.Caret);
        if (end <= start)
        {
            Say("the caret isn't on a name", error: true);
            return;
        }

        string name = code.Document.LineAt(code.Document.Caret.Line)[start..end];
        HexDocument here = document;
        AskText("Rename", $"Every {name} in {document.Name} as a word of its own becomes", name, "Rename", renamed =>
        {
            if (renamed.Length == 0 || renamed == name || !Regex.IsMatch(renamed, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                Say($"{renamed} can't be a name", error: true);
                return;
            }

            // The other files with it, looked for off the editor's thread, a question only if there are any
            List<string> roots = WorkspaceRoots();
            string? own = here.Path is { } path ? Path.GetFullPath(path) : null;
            InBackground($"looking for {name} in the other files...", () => FindIn(roots, name, new UIFindOptions(MatchCase: true, WholeWord: true))
                .Select(result => Path.GetFullPath(result.Path)).Where(file => !file.Equals(own, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), others =>
            {
                if (!documents.Contains(here))
                    return;

                if (others.Count == 0)
                {
                    RenameIn(here, name, renamed);
                    return;
                }

                string listed = string.Join(", ", others.Take(5).Select(Path.GetFileName)) + (others.Count > 5 ? $" and {others.Count - 5} more" : string.Empty);
                UIDialog.Show(chrome, "Rename", $"{name} is in {others.Count} other {(others.Count == 1 ? "file" : "files")} of the workspace as well, {listed}. Each is opened with the rename one step to undo, nothing is saved.",
                    new DialogChoice("Every file", () => RenameEverywhere(here, others, name, renamed)),
                    new DialogChoice("This file only", () => RenameIn(here, name, renamed)),
                    new DialogChoice("Cancel"));
            }, quiet: true);
        });
    }

    // A name as a word of its own
    private static string NamePattern(string name) => $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])";

    /// <summary>A name renamed in one document as one step of its undo, how many places it was.</summary>
    private int RenameIn(HexDocument target, string name, string renamed, bool say = true)
    {
        string pattern = NamePattern(name);
        string text = target.Editor.Text;
        int count = Regex.Matches(text, pattern).Count;
        if (count > 0)
            Rewrite(target, Regex.Replace(text, pattern, renamed));
        if (say)
            Say($"renamed {name} to {renamed}, {count} places");
        return count;
    }

    private void RenameEverywhere(HexDocument here, List<string> others, string name, string renamed)
    {
        int places = documents.Contains(here) ? RenameIn(here, name, renamed, say: false) : 0;
        int files = places > 0 ? 1 : 0;
        var skipped = new List<string>();

        foreach (string file in others)
        {
            // A layout open in its designer is written out of the layout, its code isn't the place to rename in
            HexDocument? open = OpenDocument(file);
            if (open is { IsText: false })
            {
                skipped.Add(Path.GetFileName(file));
                continue;
            }

            if (open is null)
            {
                if (!File.Exists(file))
                    continue;
                OpenText(file, File.ReadAllText(file), show: false);
                open = OpenDocument(file);
            }

            if (open is not null && RenameIn(open, name, renamed, say: false) is > 0 and var count)
            {
                places += count;
                files++;
            }
        }

        tabsDirty = true;
        string left = skipped.Count > 0 ? $", not in {string.Join(", ", skipped)} (open in the designer, rename it there)" : string.Empty;
        Say($"renamed {name} to {renamed}, {places} places in {files} files, none saved yet{left}");
    }

    /* Swatches */

    /// <summary>A swatch of a colour written in the code was clicked, a colour picker for another, written back as a vec.</summary>
    private void PickSwatch(HidlSwatch swatch)
    {
        ColorPicker picker = null!;
        UIDialog shown = UIDialog.Show(chrome, "Colour", "The colour written at this place in the code", new DialogChoice("Use it", () => WriteSwatch(swatch, picker.Color)), new DialogChoice("Cancel"));
        picker = layout.Instantiate("colour_dialog.hor", shown.Body).Get<ColorPicker>("picker");
        picker.Color = swatch.Color;
    }

    private void WriteSwatch(HidlSwatch swatch, Vector4 color)
    {
        string line = code.Document.LineAt(swatch.Line);
        if (swatch.Column + swatch.Length > line.Length)
            return;

        static string N(float value) => MathF.Round(value, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string written = $"vec({N(color.X)}, {N(color.Y)}, {N(color.Z)}, {N(color.W)})";

        code.Document.Select(new UITextPosition(swatch.Line, swatch.Column), new UITextPosition(swatch.Line, swatch.Column + swatch.Length));
        code.Document.Insert(written);
        Say($"the colour is {written}");
    }
}
