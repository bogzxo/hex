namespace Hex.Dawn;

// The self test, peeking at where a name is declared, Alt+F12, without leaving the line it's used on.
internal sealed partial class DawnScene
{
    private void TestPeek(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "peek.hor");
        test.Run(() =>
        {
            File.WriteAllText(file, "// how fast\nlet speed = 3;\n\nlet doubled = speed * 2;\n");
            Open(file);
            code.GoTo(3, 15);
            PeekDefinition();
        });
        test.WaitFor(() => textDialog is { IsOpen: true } && peeking is null, 4.0f);
        test.Check("peek: Alt+F12 shows where the name is declared, the line lit, over the code", () =>
            textDialog is { IsOpen: true } && Descendants(textDialog.Body).OfType<CodeEditor>().FirstOrDefault() is { ReadOnly: true } box
            && box.Text.Contains("let speed = 3;", StringComparison.Ordinal) && box.LineHighlights.Count == 1
            && box.Text.Replace("\r\n", "\n").Split('\n')[box.LineHighlights[0].Line].StartsWith("let speed", StringComparison.Ordinal));
        test.Check("peek: and the caret is still where it was", () => document.Path is { } open && PlatformPaths.Same(open, file) && code.Document.Caret.Line == 3);

        test.Run(() =>
        {
            textDialog?.Close();
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
