namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDiagnosticLocations(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "related diagnostics");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "duplicate.cpp");
            File.WriteAllText(file, "int value = 1;\nint value = 2;\n");
            explorer.OpenFolder(root);
            Open(file);
            code.Document.MoveTo(new(1, 5));
        });
        test.WaitFor(() => languages.RelatedDiagnosticsAtCaret().Count > 0, 30f);
        test.Check("Related diagnostics: clangd supplies previous declaration", () => languages.RelatedDiagnosticsAtCaret().Any(location => location.Line == 0 && location.Column == 4));
        test.Check("Related diagnostics: command is available at error", () => AllCommands().Any(command => command.Text.EndsWith(Said("code_server.related_diagnostic"), StringComparison.Ordinal)));
        test.Run(() => AllCommands().First(command => command.Text.EndsWith(Said("code_server.related_diagnostic"), StringComparison.Ordinal)).Picked?.Invoke());
        test.Check("Related diagnostics: command navigates to previous declaration", () => code.Document.Caret.Line == 0 && code.Document.Caret.Column == 4);
    }
}
