namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestOrganizeImports(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "organize imports"), file = Path.Combine(root, "main.py");
        const string original = "import sys\nimport os\n\nprint(sys.version, os.name)\n";
        string before = "";
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, original);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "codeActionProvider"), 30f);
        test.Check("Organize imports: native command is available", () => AllCommands().Any(command => command.Text.Contains(Said("code_server.organize_imports"), StringComparison.Ordinal)));
        test.Run(() =>
        {
            code.Text += "# keep this unsaved comment\n";
            before = code.Text;
        });
        test.Wait();
        test.Run(() => AllCommands().First(command => command.Text.Contains(Said("code_server.organize_imports"), StringComparison.Ordinal)).Picked?.Invoke());
        test.WaitFor(() => code.Text.ReplaceLineEndings("\n").StartsWith("import os\nimport sys", StringComparison.Ordinal), 15f);
        test.Check("Organize imports: Pyright sorts native imports and preserves other text", () => code.Text.ReplaceLineEndings("\n").StartsWith("import os\nimport sys", StringComparison.Ordinal)
            && code.Text.Contains("print(sys.version, os.name)") && code.Text.Contains("# keep this unsaved comment"));
        test.Check("Organize imports: disk is unchanged", () => File.ReadAllText(file) == original);
        test.Run(() => code.Document.Undo());
        test.Check("Organize imports: one undo restores prior unsaved buffer", () => code.Text == before);
    }
}
