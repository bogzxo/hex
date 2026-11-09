namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestHeaderPathCompletion(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "header path completion");
        string file = Path.Combine(root, "main.cpp");
        const string source = "#include \"widgets/answer-\"\nint main() { return answer(); }\n";
        const string completed = "#include \"widgets/answer-value.h\"\nint main() { return answer(); }\n";
        string before = "";
        UITextPosition caret = default;
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "widgets"));
            File.WriteAllText(Path.Combine(root, "widgets", "answer-value.h"), "#pragma once\ninline int answer() { return 42; }\n");
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.CanServe(document, "completionProvider"), 30f);
        test.Run(() => { code.Focus(); code.GoTo(0, "#include \"widgets/answer-".Length); });
        test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed("v")));
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.Contains("answer-value.h", StringComparison.Ordinal)), 15f);
        test.Check("Header path: clangd offers a hyphenated file in a subdirectory", () => code.CompletionOpen
            && code.Completions.Any(item => item.Text.Contains("answer-value.h", StringComparison.Ordinal)));
        test.Run(() => Engine.Input.Post(Horizon.Input.InputEvent.Typed("a")));
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.Contains("answer-value.h", StringComparison.Ordinal)
            && item.ExpectedVersion == code.Document.Version), 15f);
        test.Check("Header path: further typing refreshes the native replacement range", () => code.Completions.Any(item =>
            item.Text.Contains("answer-value.h", StringComparison.Ordinal) && item.ReplacementStart is not null && item.ExpectedVersion == code.Document.Version));
        test.Run(() =>
        {
            before = code.Text;
            caret = code.Document.Caret;
            int index = code.Completions.ToList().FindIndex(item => item.Text.Contains("answer-value.h", StringComparison.Ordinal));
            if (index >= 0) { code.MoveCompletion(index); code.AcceptCompletion(); }
            Console.WriteLine("[Hex self-test] header path accepted: " + code.Document.LineAt(0));
        });
        test.Check("Header path: acceptance replaces the native path range without duplication", () => code.Text == completed);
        test.Check("Header path: completion preserves the disk file", () => File.ReadAllText(file) == source);
        test.Run(() => code.Document.Undo());
        test.Check("Header path: one undo restores the typed path and caret", () => code.Text == before && code.Document.Caret == caret);
        test.Run(() => code.Document.Redo());
        test.Check("Header path: redo restores the completed include", () => code.Text == completed);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 15f);
        test.Check("Header path: clangd validates the completed source", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
    }
}
