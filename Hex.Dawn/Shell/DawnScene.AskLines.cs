using System.Numerics;

namespace Hex.Dawn;

// A dialog that asks for more than a line, a commit's message. The box is a little code editor, coloured as the
// language it's asked in says (Markdown for a commit), wrapped at its edge, Enter a new line and Ctrl+Enter the
// button. A line under it says something about what's typed as it's typed (how long a commit's summary is).
internal sealed partial class DawnScene
{
    // How big the box is, a commit message's worth
    private static readonly Vector2 LinesBoxSize = new(620.0f, 200.0f);

    private void AskLines(string title, string message, string start, string go, string language, Func<string, (string Text, bool Warning)>? note, Action<string> answered)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        CodeEditor box = null!;
        UIDialog shown = UIDialog.Show(chrome, title, message,
            new DialogChoice(go, () => answered(box.Text)),
            new DialogChoice(Said("keys.leave_it")));

        box = shown.Body.Add(new CodeEditor
        {
            Size = LinesBoxSize,
            LineNumbers = false,
            WordWrap = true,
            StickyScroll = false,
            IndentGuides = false,
            Minimap = false,
            TextScale = CODE_TEXT,
            Highlighter = CodeLanguages.ForFile(language)
        });
        box.Text = start;

        var hint = shown.Body.Add(new Label { TextScale = 0.13f, Color = DimColor });
        void Noted()
        {
            if (note?.Invoke(box.Text) is not { } said)
                return;
            hint.Text = said.Text;
            hint.Color = said.Warning ? WarningColor : DimColor;
        }

        Noted();
        box.OnChanged = Noted;
        box.OnControlEnter = () =>
        {
            shown.Close();
            answered(box.Text);
            return true;
        };

        box.Focus();
        textDialog = shown;
    }
}
