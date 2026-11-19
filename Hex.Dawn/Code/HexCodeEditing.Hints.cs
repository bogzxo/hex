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

    internal void ShowCodeMenu(HexDocument at, Vector2 point, Action<CodeEditor, UITextPosition, Menu>? addSpelling = null)
    {
        CodeEditor editor = at.Editor;

        // A right click outside of what is selected puts the caret there first, the way every editor does
        UITextPosition clicked = editor.PositionAt(point);
        if (!editor.Document.HasSelection || clicked < editor.Document.SelectionStart || clicked > editor.Document.SelectionEnd)
            editor.Document.MoveTo(clicked);

        bool hasSelection = editor.Document.HasSelection;
        var shown = new Menu("code");
        addSpelling?.Invoke(editor, clicked, shown);
        if (shown.Items.Count > 0) shown.AddSeparator();
        shown.Add(context.Strings.Said("code_hints.go_to_declared"), GoToDefinition, "F12", "target");
        shown.Add(context.Strings.Said("code_hints.find_everywhere"), FindReferences, "Shift+F12", "references");
        shown.Add(context.Strings.Said("code_hints.rename_it"), RenameSymbol, "F2", "edit");
        shown.AddSeparator();
        shown.Add(context.Strings.Said("code_hints.cut"), () => { UIClipboard.Copy(editor.Document.SelectedText); editor.Document.Insert(string.Empty); }, "Ctrl+X", "diff-removed").IsEnabled = () => hasSelection && !editor.ReadOnly;
        shown.Add(context.Strings.Said("code_hints.copy"), () => UIClipboard.Copy(editor.Document.SelectedText), "Ctrl+C", "copy").IsEnabled = () => hasSelection;
        shown.Add(context.Strings.Said("code_hints.paste"), () => { editor.Focus(); UIClipboard.Paste(); }, "Ctrl+V", "clippy").IsEnabled = () => !editor.ReadOnly;
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
    /// F2 asks the language server for a semantic rename. HIDL retains its explicitly word-based prompt, with one
    /// undo step per document and no implicit saves. Other languages require a ready rename service.
    /// </summary>
    internal void RenameSymbol()
    {
        // A file of a language server's is renamed in the way the server knows the name, everywhere it's that name
        HexDocument asked = document;
        if (languages?.Rename(asked) == true)
            return;

        // HIDL's explicitly word-based operation predates language servers. Other languages must not silently
        // substitute text matching for a semantic rename while their server is absent, starting or unsupported.
        if (!ReferenceEquals(asked.Editor.Highlighter, CodeLanguages.Hidl))
        {
            context.Notices.Say(context.Strings.Said("code_server.rename_unavailable"), error: true);
            return;
        }

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

    // The most colours of the file offered under the picker, and how big each is across
    private const int SWATCH_CHOICES = 14;
    private const float SWATCH_CHIP = 22.0f, SWATCH_FIELD = 30.0f;

    /// <summary>
    /// A swatch of a colour written in the code was clicked, a picker for another colour, written back as a vec. The
    /// picker with what it was beside what it's becoming, the colour as hex and as red, green, blue and alpha to type, and
    /// the other colours the file has already to pick from. Enter in any of the boxes applies it.
    /// </summary>
    internal void PickSwatch(HidlSwatch swatch)
    {
        ColorPicker picker = null!;
        UIDialog shown = null!;
        void Apply()
        {
            WriteSwatch(swatch, picker.Color);
            shown.Close();
        }

        shown = UIDialog.Show(chrome, context.Strings.Said("code_hints.colour"), SwatchMessage(swatch),
            new DialogChoice(context.Strings.Said("code_hints.use_it"), () => WriteSwatch(swatch, picker.Color)), new DialogChoice(context.Strings.Said("keys.leave_it")));
        picker = layout.Instantiate("colour_dialog.hor", shown.Body).Get<ColorPicker>("picker");
        picker.Original = swatch.Color;
        picker.Color = swatch.Color;

        // The colour typed, as hex and as its four parts, each kept in step with the picker and with each other
        bool syncing = false;
        var hex = new TextBox { Size = new Vector2(112.0f, SWATCH_FIELD), MaxLength = 9, OnSubmitted = _ => Apply() };
        NumberBox Part(string tip) => new() { Size = new Vector2(52.0f, SWATCH_FIELD), Tooltip = tip, OnSubmitted = _ => Apply() };
        NumberBox red = Part(context.Strings.Said("code_hints.colour_red")), green = Part(context.Strings.Said("code_hints.colour_green")),
            blue = Part(context.Strings.Said("code_hints.colour_blue")), opacity = Part(context.Strings.Said("code_hints.colour_alpha"));

        void Show(Vector4 colour, UIComponent? typedIn)
        {
            syncing = true;
            if (typedIn != hex) hex.Text = ColorPicker.ToHex(colour);
            if (typedIn != red) red.Value = MathF.Round(colour.X * 255.0f);
            if (typedIn != green) green.Value = MathF.Round(colour.Y * 255.0f);
            if (typedIn != blue) blue.Value = MathF.Round(colour.Z * 255.0f);
            if (typedIn != opacity) opacity.Value = MathF.Round(colour.W * 100.0f);
            syncing = false;
        }

        void Typed(Vector4 colour, UIComponent from)
        {
            if (syncing)
                return;
            picker.Color = Vector4.Clamp(colour, Vector4.Zero, Vector4.One);
            Show(picker.Color, from);
        }

        picker.OnChanged = colour => Show(colour, picker);
        hex.OnChanged = typed =>
        {
            if (ColorPicker.FromHex(typed) is { } colour)
                Typed(colour, hex);
        };
        red.OnValueChanged = value => Typed(picker.Color with { X = value / 255.0f }, red);
        green.OnValueChanged = value => Typed(picker.Color with { Y = value / 255.0f }, green);
        blue.OnValueChanged = value => Typed(picker.Color with { Z = value / 255.0f }, blue);
        opacity.OnValueChanged = value => Typed(picker.Color with { W = value / 100.0f }, opacity);
        Show(picker.Color, null);

        // Each box with what it is over it, small and quiet
        var fields = shown.Body.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        foreach (var (caption, box) in new (string, UIComponent)[] { ("Hex", hex), ("R", red), ("G", green), ("B", blue), (context.Strings.Said("code_hints.colour_alpha_short"), opacity) })
        {
            var column = fields.Add(new StackPanel { Spacing = 3.0f, Anchor = Origin.Bottom, Pivot = Origin.Bottom });
            column.Add(new Label { Text = caption, TextScale = 0.12f, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
            column.Add(box);
        }

        // The colours the file has already, one press to take one
        var others = code.Swatches.Where(other => other != swatch).Select(other => other.Color).Distinct().Take(SWATCH_CHOICES).ToList();
        if (others.Count > 0)
        {
            shown.Body.Add(new Label { Text = context.Strings.Said("code_hints.colour_in_file"), TextScale = 0.12f, Dim = true, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left });
            var chips = shown.Body.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6.0f, Anchor = Origin.Left, Pivot = Origin.Left });
            foreach (Vector4 other in others)
            {
                Vector4 taken = other;
                chips.Add(new SwatchChip(taken, () =>
                {
                    picker.Color = taken;
                    picker.Changed();
                }) { Size = new Vector2(SWATCH_CHIP), Tooltip = ColorPicker.ToHex(taken) });
            }
        }
    }

    // What the dialog says of the colour, the key it's the value of and the line, or the line alone
    private string SwatchMessage(HidlSwatch swatch)
    {
        string line = code.Document.LineAt(swatch.Line);
        string before = line[..Math.Min(swatch.Column, line.Length)];
        Match key = Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)\s*[:=]\s*$");
        string where = (swatch.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return key.Success
            ? context.Strings.Said("code_hints.colour_text_key", ("key", key.Groups[1].Value), ("line", where))
            : context.Strings.Said("code_hints.colour_text", ("line", where));
    }

    /// <summary>A colour to press, a little rounded square of it, over a checkerboard when it isn't opaque.</summary>
    private sealed class SwatchChip(Vector4 colour, Action pressed) : UIComponent
    {
        private const float ROUND = 4.0f;

        protected override bool HitTestVisible => true;

        protected override Vector2 Measure(Horizon.UI.Skinning.UISkin skin) => new(SWATCH_CHIP);

        protected override void Paint(Horizon.UI.Drawing.UIDrawList list)
        {
            if (colour.W < 1.0f)
            {
                list.RoundRect(Bounds, ROUND, new Vector4(0.8f, 0.8f, 0.8f, 1.0f));
                list.Rect(new UIRect(Bounds.Min, Bounds.Center), new Vector4(0.55f, 0.55f, 0.55f, 1.0f));
                list.Rect(new UIRect(Bounds.Center, Bounds.Max), new Vector4(0.55f, 0.55f, 0.55f, 1.0f));
            }

            list.RoundRect(Bounds, ROUND, colour);
            var skin = list.Skin;
            Vector4 edge = IsHovered ? skin.TextColor : skin.BorderColor.W > 0.0f ? skin.BorderColor : skin.TextColor * new Vector4(1, 1, 1, 0.25f);
            list.RoundOutline(Bounds, ROUND, IsHovered ? 2.0f : 1.0f, edge);
        }

        protected override void OnPointerUp(Vector2 point)
        {
            if (Bounds.Contains(point))
                pressed();
        }
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
