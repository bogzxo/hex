namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDiagnosticDocumentation(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "diagnostic documentation");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "main.py");
            File.WriteAllText(file, "value = missing_name\n");
            explorer.OpenFolder(root);
            Open(file);
            code.Document.MoveTo(new(0, 10));
        });
        test.WaitFor(() => languages.ServerDiagnosticsOf(document)?.Any(diagnostic => diagnostic.DocumentationUrl is not null) == true, 30f);
        test.Check("Diagnostic documentation: Pyright supplies native rule documentation", () => languages.DiagnosticDocumentationAtCaret()?.Contains("reportUndefinedVariable") == true);
        test.Check("Diagnostic documentation: command available at error", () => AllCommands().Any(command => command.Text.EndsWith(Said("code_server.diagnostic_documentation"), StringComparison.Ordinal)));
        test.Run(() => code.Document.MoveTo(new(0, 0)));
        test.Check("Diagnostic documentation: command hidden away from diagnostic", () => languages.DiagnosticDocumentationAtCaret() is null);
    }
}
