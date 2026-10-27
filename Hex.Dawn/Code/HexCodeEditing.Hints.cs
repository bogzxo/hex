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
internal sealed partial class HexCodeEditing
{
    /// <summary>
    /// The hint for the word at a place, markdown, null for nothing worth saying. What it is is worked out from what is
    /// round it the way the highlighter does it.
    /// </summary>
    internal string? HoverHint(CodeEditor editor, UITextPosition at)
    {
        // What a part says about some files first, the terminal's its settings, commands and functions (HexTerminalFiles)
        if (hoverSources.Count > 0 && documents.FirstOrDefault(open => open.Editor == editor) is { } of)
        {
            foreach (var source in hoverSources)
            {
                if (source(of, editor, at) is { } said)
                    return said;
            }
        }

        // The rest is HIDL's, worked out as the language server works it out, with what the run and the workspace know
        return HidlAnalysis.Hover(editor.Text, at.Line, at.Column, name => DocOf(name) is { } doc ? new HidlDocumented(doc.Doc, doc.Where) : null);
    }

    /* The editor's context menu */

    internal void ShowCodeMenu(HexDocument at, Vector2 point)
    {
        CodeEditor editor = at.Editor;

        // A right click outside of what is selected puts the caret there first, the way every editor does
        UITextPosition clicked = editor.PositionAt(point);
        if (!editor.Document.HasSelection || clicked < editor.Document.SelectionStart || clicked > editor.Document.SelectionEnd)
            editor.Document.MoveTo(clicked);

        bool hasSelection = editor.Document.HasSelection;
        var shown = new Menu("code");
        shown.Add(context.Strings.Said("code_hints.go_to_declared"), GoToDefinition, "F12", "target");
        shown.Add(context.Strings.Said("code_hints.find_everywhere"), FindReferences, "Shift+F12", "references");
        shown.Add(context.Strings.Said("code_hints.rename_it"), RenameSymbol, "F2", "edit");
        shown.AddSeparator();
        shown.Add(context.Strings.Said("code_hints.cut"), () => { UIClipboard.Copy(editor.Document.SelectedText); editor.Document.Insert(string.Empty); }, "Ctrl+X", "diff-removed").IsEnabled = () => hasSelection && !editor.ReadOnly;
        shown.Add(context.Strings.Said("code_hints.copy"), () => UIClipboard.Copy(editor.Document.SelectedText), "Ctrl+C", "copy").IsEnabled = () => hasSelection;
        shown.Add(context.Strings.Said("code_hints.select_all"), () => editor.Document.SelectAll(), "Ctrl+A", "selection");
        shown.AddSeparator();
        shown.Add(context.Strings.Said("code_hints.toggle_comment"), () => editor.Document.ToggleComment(), "Ctrl+/", "comment");
        shown.Add(context.Strings.Said("code.toggle_block_comment"), () => editor.Document.ToggleBlockComment(), "Shift+Alt+A", "comment-discussion");
        shown.Add(context.Strings.Said("code_hints.duplicate_line"), () => editor.Document.CopyLines(1), "Shift+Alt+Down", "arrow-circle-down");
        shown.Add(context.Strings.Said("code_hints.next_occurrence"), () => editor.Document.AddNextOccurrence(), "Ctrl+D", "add");
        shown.AddSeparator();
        window.AddCodeMenu(shown);

        editor.Focus();
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    /// <summary>
    /// F2, a name renamed everywhere it is in the file as a word of its own, one step to undo. When other files of the
    /// workspace have it too (a key of an included file, the shared moves and a fighter's own), it asks whether to rename
    /// it in them as well, each opened behind with the rename as one step of its undo and nothing saved.
    /// </summary>
    internal void RenameSymbol()
    {
        // A file of a language server's is renamed in the way the server knows the name, everywhere it's that name
        HexDocument asked = document;
        if (languages?.Rename(asked, (name, renamed) => RenameByWords(asked, name, renamed, codeOnly: true)) == true)
            return;

        var (start, end) = code.Document.WordAt(code.Document.Caret);
        if (end <= start)
        {
            context.Notices.Say(context.Strings.Said("code_hints.caret_not_on_name"), error: true);
            return;
        }

        string name = code.Document.LineAt(code.Document.Caret.Line)[start..end];
        HexDocument here = document;
        context.Windows.Prompt(context.Strings.Said("code_hints.rename"), context.Strings.Said("code_hints.rename_prompt", ("name", name), ("document", document.Name)), name, context.Strings.Said("code_hints.rename"), string.Empty,
            renamed => RenameByWords(here, name, renamed));
    }

    /// <summary>
    /// A name renamed as a word of its own, in a document and, when other files of the workspace have it too, in them if
    /// that's wanted. In the code alone for a language a server can't rename in (D), never in a text or a comment that says
    /// it, renaming area changed "area " too.
    /// </summary>
    private void RenameByWords(HexDocument here, string name, string renamed, bool codeOnly = false)
    {
        if (renamed.Length == 0 || renamed == name || !Regex.IsMatch(renamed, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            context.Notices.Say(context.Strings.Said("code_hints.not_a_name", ("renamed", renamed)), error: true);
            return;
        }

        // The other files with it, looked for off the editor's thread, a question only if there are any
        List<string> roots = [.. context.Workspace.Roots];
        string? own = here.Path is { } path ? Path.GetFullPath(path) : null;
        context.Work.InBackground(context.Strings.Said("code_hints.looking_in_others", ("name", name)),() => HexSearch.FindIn(roots, name, new UIFindOptions(MatchCase: true, WholeWord: true))
            .Select(result => Path.GetFullPath(result.Path)).Where(file => !file.Equals(own, PlatformPaths.Comparison)).Distinct(PlatformPaths.Comparer).ToList(), others =>
        {
            if (!documents.Contains(here))
                return;

            if (others.Count == 0)
            {
                RenameIn(here, name, renamed, codeOnly: codeOnly);
                return;
            }

            string listed = string.Join(", ", others.Take(5).Select(Path.GetFileName)) + (others.Count > 5 ? context.Strings.Said("code_hints.and_more", ("count", (others.Count - 5).ToString())) : string.Empty);
            UIDialog.Show(chrome, context.Strings.Said("code_hints.rename"), context.Strings.Said(others.Count == 1 ? "code_hints.in_other_file" : "code_hints.in_other_files", ("name", name), ("count", others.Count.ToString()), ("listed", listed)),
                new DialogChoice(context.Strings.Said("code_hints.every_file"), () => RenameEverywhere(here, others, name, renamed, codeOnly)),
                new DialogChoice(context.Strings.Said("code_hints.this_file_only"), () => RenameIn(here, name, renamed, codeOnly: codeOnly)),
                new DialogChoice(context.Strings.Said("keys.leave_it")));
        }, quiet: true);
    }

    // A name as a word of its own
    private static string NamePattern(string name) => $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])";

    /// <summary>A name renamed in one document as one step of its undo, how many places it was. In the code alone when it says so.</summary>
    private int RenameIn(HexDocument target, string name, string renamed, bool say = true, bool codeOnly = false)
    {
        string text = target.Editor.Text;
        int count = 0;
        string result = Regex.Replace(text, NamePattern(name), match =>
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
            if (match.Index == 0)
                lineStart = 0;
            int lineEnd = text.IndexOf('\n', match.Index) is var end and >= 0 ? end : text.Length;
            if (codeOnly && HexSearch.InTextOrComment(text[lineStart..lineEnd], match.Index - lineStart))
                return match.Value;

            count++;
            return renamed;
        });
        if (count > 0)
            context.Documents.Rewrite(target, result);
        if (say)
            context.Notices.Say(context.Strings.Said("code_hints.renamed", ("name", name), ("renamed", renamed), ("count", count.ToString())));
        return count;
    }

    private void RenameEverywhere(HexDocument here, List<string> others, string name, string renamed, bool codeOnly = false)
    {
        int places = documents.Contains(here) ? RenameIn(here, name, renamed, say: false, codeOnly: codeOnly) : 0;
        int files = places > 0 ? 1 : 0;
        var skipped = new List<string>();

        foreach (string file in others)
        {
            // A layout open in its designer is written out of the layout, its code isn't the place to rename in
            HexDocument? open = context.Documents.Find(file);
            if (open is { IsText: false })
            {
                skipped.Add(Path.GetFileName(file));
                continue;
            }

            if (open is null)
            {
                if (!File.Exists(file))
                    continue;
                context.Documents.OpenBehind(file);
                open = context.Documents.Find(file);
            }

            if (open is not null && RenameIn(open, name, renamed, say: false, codeOnly: codeOnly) is > 0 and var count)
            {
                places += count;
                files++;
            }
        }

        tabsDirty = true;
        string left = skipped.Count > 0 ? context.Strings.Said("code_hints.skipped_in_designer", ("skipped", string.Join(", ", skipped))) : string.Empty;
        context.Notices.Say(context.Strings.Said("code_hints.renamed_everywhere", ("name", name), ("renamed", renamed), ("places", HexText.Count("place", places)), ("files", HexText.Count("file", files)), ("left", left)));
    }

    /* Swatches */

    /// <summary>A swatch of a colour written in the code was clicked, a colour picker for another, written back as a vec.</summary>
    internal void PickSwatch(HidlSwatch swatch)
    {
        ColorPicker picker = null!;
        UIDialog shown = UIDialog.Show(chrome, context.Strings.Said("code_hints.colour"), context.Strings.Said("code_hints.colour_text"), new DialogChoice(context.Strings.Said("code_hints.use_it"), () => WriteSwatch(swatch, picker.Color)), new DialogChoice(context.Strings.Said("keys.leave_it")));
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
        context.Notices.Say(context.Strings.Said("code_hints.colour_is", ("written", written)));
    }
}
