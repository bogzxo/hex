using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCppIncludes(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "cpp include completion");
        string file = Path.Combine(root, "main.cpp");
        const string original = "int main() {\n    std::vec<int> values;\n    return values.size();\n}\n";
        const string completed = "#include <vector>\nint main() {\n    std::vector<int> values;\n    return values.size();\n}\n";
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, original);
            explorer.OpenFolder(root);
            Open(file);
        });
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: > 0 }, 30);
        test.Run(() =>
        {
            code.Focus();
            code.Document.MoveTo(new(1, 12));
            code.Complete();
        });
        // clangd may answer the first completion before its standard-library index is available.
        test.WaitFor(() => LanguageServers.Running.FirstOrDefault(server => server.LanguageId == "cpp" && PlatformPaths.Same(server.RootDirectory, root))
            ?.ErrorOutput.Contains("Indexed c++", StringComparison.Ordinal) == true, 15);
        test.Run(() => code.Complete());
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Insert == "vector" && item.AdditionalEdits is { Count: > 0 }), 20);
        test.Run(() =>
        {
            if (!code.Completions.Any(item => item.Insert == "vector"))
            {
                Console.WriteLine("[Hex self-test] C++ include completions: " + string.Join(", ", code.Completions.Select(item => item.Text + " => " + item.Insert)));
                Console.WriteLine(LanguageServers.Running.FirstOrDefault(server => server.LanguageId == "cpp" && PlatformPaths.Same(server.RootDirectory, root))?.ErrorOutput);
            }
        });
        test.Check("C++ include: clangd offers vector with its header", () => code.CompletionOpen
            && code.Completions.Any(item => item.Insert == "vector" && item.AdditionalEdits?.Any(edit => edit.Text.Contains("#include <vector>")) == true));
        test.Run(() => code.AcceptCompletion());
        test.Check("C++ include: completion adds the header and preserves template arguments", () => code.Text == completed);
        test.Check("C++ include: caret follows the symbol before its template arguments", () => code.Document.Caret == new UITextPosition(2, 15));
        test.WaitFor(() => code.Text != completed || languages.ServerDiagnosticsOf(document) is { Count: 0 }, 20);
        test.Check("C++ include: clangd validates the resulting source", () => languages.ServerDiagnosticsOf(document) is { Count: 0 });
        test.Run(() => code.Document.Undo());
        test.Check("C++ include: one undo restores both edits and the caret", () => code.Text == original && code.Document.Caret == new UITextPosition(1, 12));
        test.Run(() => code.Document.Redo());
        test.Check("C++ include: redo restores both edits without implicitly saving", () => code.Text == completed && File.ReadAllText(file) == original);
        test.Run(() => { if (code.Text == completed) { debugger.ToggleBreakpoint(file, 3); RunOrPlay(); } });
        test.WaitFor(() => code.Text != completed || debugger.DebugStopped, 45);
        test.Check("C++ include: native compilation and LLDB reach the source breakpoint", () => debugger.DebugStopped
            && code.ExecutionLine == 3 && File.ReadAllText(file) == completed);
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Check("C++ include: debug session stops cleanly", () => !debugger.Debugging);
        test.Run(() => debugger.breakpoints.Remove(file));
    }
}
