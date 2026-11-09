namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestPythonRename(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "python rename");
        string module = Path.Combine(root, "library.py"), caller = Path.Combine(root, "main.py"), other = Path.Combine(root, "unrelated.py");
        const string originalModule = "def answer() -> int:\n    return 42\n";
        const string originalCaller = "from library import answer\nvalue = answer()\n";
        const string unrelated = "def answer() -> str:\n    return 'unrelated'\n";
        string before = "";
        HexDocument? callerDocument = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(module, originalModule);
            File.WriteAllText(caller, originalCaller);
            File.WriteAllText(other, unrelated);
            explorer.OpenFolder(root);
            Open(caller);
            callerDocument = document;
        });
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 30);
        test.Check("Python rename: caller is analyzed while the definition remains unopened", () =>
            languages.ServerDiagnosticsOf(document) is { Count: 0 } && Context.Documents.Find(module) is null);
        test.Run(() => { code.Document.MoveTo(new(0, 1)); languages.Rename(document); });
        test.Settle(1f);
        test.Check("Python rename: keyword preparation is rejected before opening a prompt", () => textDialog is not { IsOpen: true } && code.Text == originalCaller);
        test.Run(() =>
        {
            code.Document.MoveTo(new(1, 10));
            languages.Rename(document);
            code.Document.MoveTo(new(0, 0));
        });
        test.Settle(1f);
        test.Check("Python rename: moving the caret discards a pending preparation", () => textDialog is not { IsOpen: true });
        test.Run(() => { code.Document.MoveTo(new(1, 10)); languages.Rename(document); });
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Run(() =>
        {
            if (textDialog is not { IsOpen: true } dialog) return;
            var input = Descendants(dialog.Body).OfType<TextBox>().First();
            Open(other);
            input.Text = "stale_rename";
            input.OnSubmitted?.Invoke("stale_rename");
        });
        test.Settle(0.5f);
        test.Check("Python rename: stale prompt cannot rename after switching documents", () =>
            callerDocument!.Editor.Text == originalCaller && File.ReadAllText(module) == originalModule
            && (Context.Documents.Find(module)?.Editor.Text ?? originalModule) == originalModule);
        test.Run(() => Open(caller));
        test.Run(() =>
        {
            code.Text += "# answer in this unsaved comment must remain\n";
            before = code.Text;
            code.Document.MoveTo(new(1, 10));
            languages.Rename(document);
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Check("Python rename: rename prompt opens", () => textDialog is { IsOpen: true });
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog)
            {
                var input = Descendants(dialog.Body).OfType<TextBox>().First();
                input.Text = "project_answer";
                input.OnSubmitted?.Invoke("project_answer");
            }
        });
        test.WaitFor(() => Context.Documents.Find(module)?.Editor.Text.Contains("def project_answer()") == true, 20);
        test.Check("Python rename: definition, import and call change together", () =>
            Context.Documents.Find(module)?.Editor.Text.Contains("def project_answer()") == true
            && callerDocument!.Editor.Text.Contains("from library import project_answer\nvalue = project_answer()"));
        test.Check("Python rename: unsaved comments and unrelated symbols are preserved", () =>
            callerDocument!.Editor.Text.Contains("# answer in this unsaved comment must remain")
            && File.ReadAllText(other) == unrelated && (Context.Documents.Find(other)?.Editor.Text ?? unrelated) == unrelated);
        test.Check("Python rename: edits stay in buffers until saved", () =>
            File.ReadAllText(module) == originalModule && File.ReadAllText(caller) == originalCaller);
        test.Run(() => { callerDocument!.Editor.Document.Undo(); Context.Documents.Find(module)?.Editor.Document.Undo(); });
        test.Check("Python rename: undo restores both buffers including prior unsaved text", () =>
            callerDocument!.Editor.Text == before && Context.Documents.Find(module)?.Editor.Text == originalModule);
    }
}
