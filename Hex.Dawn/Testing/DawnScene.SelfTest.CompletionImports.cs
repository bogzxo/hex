namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCompletionImports(HexPointerScript test) => TestCompletionImports(test, firstColumn: false);

    private void TestCompletionImports(HexPointerScript test, bool firstColumn)
    {
        string root = Path.Combine(testFolder, "completion imports");
        string module = Path.Combine(root, "helper.py"), caller = Path.Combine(root, "main.py");
        string prefix = firstColumn ? "" : "value = ";
        string suffix = firstColumn ? "()" : "";
        string original = prefix + "hex_import_a" + suffix + "\n";
        string completedLine = prefix + "hex_import_answer" + suffix;
        int column = prefix.Length + 12;
        string accepted = "";
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(module, "def hex_import_answer() -> int:\n    return 42\n");
            File.WriteAllText(caller, original);
            explorer.OpenFolder(root);
            Open(module);
        });
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 30);
        test.Run(() => Open(caller));
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: > 0 }, 30);
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new(0, column));
            code.Complete();
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text == "hex_import_answer"), 20);
        test.Check("Auto-import: Pyright offers the symbol with an import edit", () =>
            code.Completions.Any(item => item.Text == "hex_import_answer" && item.AdditionalEdits is { Count: > 0 }));
        test.Run(() => code.AcceptCompletion());
        test.Check("Auto-import: accepting adds the native import and completes the symbol", () =>
            code.Text.Contains("from helper import hex_import_answer\n")
            && code.Text.Contains(completedLine + "\n"));
        test.Check("Auto-import: caret follows the completed symbol", () =>
            code.Document.LineAt(code.Document.Caret.Line) == completedLine
            && code.Document.Caret.Column == prefix.Length + 17);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 20);
        test.Check("Auto-import: Pyright validates the resulting code", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        test.Run(() => { accepted = code.Text; code.Document.Undo(); });
        test.Check("Auto-import: one undo restores the original text and caret", () =>
            code.Text == original && code.Document.Caret == new UITextPosition(0, column));
        test.Run(() => code.Document.Redo());
        test.Check("Auto-import: one redo restores both edits without saving", () => code.Text == accepted && File.ReadAllText(caller) == original);

        // A result already displayed must not apply an import to a document changed since the request.
        test.Run(() =>
        {
            code.Document.Undo();
            code.Complete();
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.AdditionalEdits is { Count: > 0 }), 20);
        string changed = "";
        test.Run(() =>
        {
            code.Document.MoveTo(new(0, 0));
            code.Document.Insert("# unsaved\n");
            code.Document.MoveTo(new(1, column));
            changed = code.Text;
            code.AcceptCompletion();
        });
        test.Check("Auto-import: stale results do not change unsaved text", () => code.Text == changed);
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.ExpectedVersion == code.Document.Version && item.AdditionalEdits is { Count: > 0 }), 20);
        test.Run(() => code.AcceptCompletion());
        test.Check("Auto-import: refreshed completion preserves the unsaved comment", () =>
            code.Text.Contains("# unsaved\n") && code.Text.Contains("from helper import hex_import_answer\n")
            && code.Text.Contains(completedLine + "\n"));
    }
}
