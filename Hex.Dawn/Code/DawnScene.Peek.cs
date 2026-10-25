using System.Numerics;

namespace Hex.Dawn;

// Peek, where something is declared shown over the code without leaving it, VS Code's Alt+F12. It goes there the way
// F12 does (HIDL's own way, a language server's, a key of a language file), notes where that was, comes straight back
// (a file it had to open for it closed again), and puts the lines round it up in a window, coloured and read only, the
// line itself lit, with a button to go there for real. A language server can take a moment to answer, so it waits up
// to a few seconds for F12 to land.
internal sealed partial class DawnScene
{
    // How long F12 is given to land, and how many lines either side of where it did the window shows
    private const float PEEK_PATIENCE = 4.0f;
    private const int PEEK_AROUND = 12;

    private static readonly Vector2 PeekSize = new(760.0f, 330.0f);

    // A peek going on, where it started from and what was open then
    private (HexDocument From, UITextPosition Caret, int Version, HashSet<HexDocument> Open, float Left)? peeking;

    private void BuildPeek()
    {
        commands.Add("Go/places", Said("peek.command"), PeekDefinition, "Alt+F12", "eye", UIKeyUse.BoundWhileTyping);
    }

    private void PeekDefinition()
    {
        if (!document.IsText || document.DiffOf is not null || peeking is not null)
            return;

        peeking = (document, code.Document.Caret, code.Document.Version, [.. documents], PEEK_PATIENCE);
        codeEditing.GoToDefinition();
        UpdatePeek(0.0f);
    }

    /// <summary>Every tick a peek is going on, whether F12 has landed yet, and if it has, back and the window up.</summary>
    private void UpdatePeek(float dt)
    {
        if (peeking is not { } peek)
            return;

        // Typed into while waiting, the peek is let go of, whatever F12 comes back with is for text that isn't there now
        if (document == peek.From && code.Document.Version != peek.Version)
        {
            peeking = null;
            return;
        }

        // Landed anywhere else, another file or another place in this one, the same line too (let x = f(x))
        bool moved = document != peek.From || code.Document.Caret != peek.Caret;
        if (!moved)
        {
            float left = peek.Left - dt;
            peeking = peek with { Left = left };
            if (left <= 0.0f)
            {
                peeking = null;
                Say(Said("peek.nothing"));
            }
            return;
        }

        peeking = null;
        HexDocument there = document;
        int line = code.Document.Caret.Line;
        string title = there.Path is { } path ? $"{Path.GetFileName(path)}:{line + 1}" : $"{there.Name}:{line + 1}";
        string text = there.Editor.Text;
        ICodeHighlighter? highlighter = there.Editor.Highlighter;
        string? target = there.Path;

        // Back where it started, a file opened only to look in it closed again
        if (!peek.Open.Contains(there))
            CloseNow();
        if (documents.Contains(peek.From))
        {
            Show(peek.From);
            code.Document.MoveTo(peek.Caret);
        }

        ShowPeek(title, text, line, highlighter, target);
    }

    private void ShowPeek(string title, string text, int line, ICodeHighlighter? highlighter, string? path)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int from = Math.Max(0, line - PEEK_AROUND), to = Math.Min(lines.Length, line + PEEK_AROUND + 1);
        string shown = string.Join('\n', lines[from..to]);

        UIDialog dialog = UIDialog.Show(chrome, title, Said("peek.text"),
            new DialogChoice(Said("peek.open"), () =>
            {
                if (path is not null)
                    OpenAt(path, line, 0);
            }),
            new DialogChoice(Said("peek.close")));

        var box = dialog.Body.Add(new CodeEditor
        {
            Size = PeekSize,
            ReadOnly = true,
            WordWrap = false,
            Minimap = false,
            StickyScroll = false,
            TextScale = CODE_TEXT,
            Highlighter = highlighter,
            FirstLineNumber = from + 1
        });
        box.Text = shown;
        box.LineHighlights = [new UICodeLineHighlight(line - from, 1, (compositor.Skin?.Palette.Selection ?? new Vector4(0.38f, 0.62f, 1.0f, 0.32f)) with { W = 0.3f })];
        box.GoTo(line - from, 0);
        textDialog = dialog;
    }
}
