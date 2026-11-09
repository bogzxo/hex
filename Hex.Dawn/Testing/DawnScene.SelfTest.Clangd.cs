using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestClangd(HexPointerScript test) => TestClangd(test, ".cpp", ".h");

    private void TestClangd(HexPointerScript test, string sourceExtension, string headerExtension, bool midWord = false)
    {
        string root = Path.Combine(testFolder, "clangd services");
        string project = Path.Combine(root, "CMakeLists.txt");
        string source = Path.Combine(root, "value" + sourceExtension), header = Path.Combine(root, "answer" + headerExtension);
        string include = "#include \"" + Path.GetFileName(header) + "\"\n";
        LanguageServer? server = null;
        Task<IReadOnlyList<LanguageServerPlace>>? references = null;
        Task<LanguageServerCompletions?>? completion = null;
        Task<string?>? hover = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(project, "cmake_minimum_required(VERSION 3.20)\nproject(Services LANGUAGES CXX)\nadd_library(value STATIC " + Path.GetFileName(source) + ")\ntarget_compile_definitions(value PRIVATE PROJECT_ANSWER=42)\n");
            File.WriteAllText(header, "#pragma once\ninline int answer() { return PROJECT_ANSWER; }\n");
            File.WriteAllText(source, include + "int value() { return answer(); }\n");
            explorer.OpenFolder(root);
            Open(project);
            debugger.ConfigureCMake();
        });
        test.WaitFor(() => !debugger.Debugging, 30);
        test.Check("clangd: project compilation database is generated", () => File.Exists(Path.Combine(root, "build", "hex-debug", "compile_commands.json")));
        test.Run(() => Open(source));
        test.WaitFor(() => (server = LanguageServers.Running.FirstOrDefault(s => s.LanguageId == "cpp" && PlatformPaths.Same(s.RootDirectory, root))) is { IsReady: true }, 30);
        test.WaitFor(() => languages.ServerDiagnosticsOf(document) is { Count: 0 }, 30);
        test.Check("clangd: header uses project compiler definitions", () => server is { IsReady: true } && languages.ServerDiagnosticsOf(document) is { Count: 0 });
        test.Run(() =>
        {
            code.Document.MoveTo(new(1, 23));
            languages.GoToDefinition(document);
        });
        test.WaitFor(() => PlatformPaths.Same(document.Path!, header), 15);
        test.Check("clangd: Go to Definition opens the header at the symbol", () => PlatformPaths.Same(document.Path!, header) && code.Document.Caret.Line == 1);
        test.Check("clangd: header supports project configuration and syntax highlighting", () => debugger.CanConfigureCMake && code.Highlighter is not null);
        test.Run(() =>
        {
            references = server?.References(header, 1, 12);
            completion = server?.Complete(source, 1, 24, 21);
            hover = server?.Hover(source, 1, 23);
        });
        test.WaitFor(() => references?.IsCompleted == true && completion?.IsCompleted == true && hover?.IsCompleted == true, 20);
        test.Check("clangd: references include the calling source file", () => references is { IsCompletedSuccessfully: true }
            && references.Result.Any(place => PlatformPaths.Same(place.Path, source) && place.Line == 1));
        test.Check("clangd: completion includes the project function", () => completion is { IsCompletedSuccessfully: true }
            && completion.Result is { } choices && choices.Items.Any(item => item.Text.TrimStart().StartsWith("answer", StringComparison.Ordinal)));
        test.Check("clangd: hover exposes the function signature", () => hover is { IsCompletedSuccessfully: true }
            && hover.Result?.Contains("answer") == true && hover.Result.Contains("int"));
        test.Run(() =>
        {
            Open(source);
            code.Text = include + (midWord ? "int value() { return ansxxx(); }\n" : "int value() { return ans(); }\n");
            code.Focus();
            code.Document.MoveTo(new(1, 24));
            code.Complete(typed: true);
        });
        test.WaitFor(() => code.CompletionOpen && code.Completions.Any(item => item.Text.TrimStart().StartsWith("answer", StringComparison.Ordinal)), 15);
        test.Check("clangd: native completion appears in the editor", () => code.CompletionOpen
            && code.Completions.Any(item => item.Text.TrimStart().StartsWith("answer", StringComparison.Ordinal)));
        if (midWord)
            test.Check("clangd: replacement range includes the suffix after the caret", () =>
                code.Completions.Any(item => item.Text.TrimStart().StartsWith("answer", StringComparison.Ordinal)
                    && item.ReplacementEnd == new UITextPosition(1, 27) && item.ExpectedVersion == code.Document.Version));
        test.Run(() => code.AcceptCompletion());
        test.Check("clangd: accepting completion preserves the surrounding call", () =>
            code.Text == include + "int value() { return answer(); }\n");
        if (midWord)
        {
            test.Check("clangd: completion leaves the caret before the call parentheses", () => code.Document.Caret == new UITextPosition(1, 27));
            test.Run(() => code.Document.Undo());
            test.Check("clangd: undo restores the original suffix and caret", () =>
                code.Text == include + "int value() { return ansxxx(); }\n" && code.Document.Caret == new UITextPosition(1, 24));
            test.Run(() => code.Document.Redo());
            test.Check("clangd: redo restores the completion", () => code.Text == include + "int value() { return answer(); }\n");
        }
        string originalSource = "", originalHeader = "";
        HexDocument? sourceDocument = null, headerDocument = null;
        test.Run(() =>
        {
            sourceDocument = document;
            headerDocument = Context.Documents.Find(header);
            code.Text += "// answer in this comment must stay unchanged\n";
            originalSource = code.Text;
            originalHeader = headerDocument!.Editor.Text;
            code.Document.MoveTo(new(1, 23));
            languages.Rename(document);
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 10);
        test.Check("clangd rename: editor offers the rename prompt", () => textDialog is { IsOpen: true });
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } dialog)
            {
                var input = Descendants(dialog.Body).OfType<TextBox>().First();
                input.Text = "projectAnswer";
                input.OnSubmitted?.Invoke("projectAnswer");
            }
        });
        test.WaitFor(() => sourceDocument?.Editor.Text.Contains("return projectAnswer()") == true
            && headerDocument?.Editor.Text.Contains("int projectAnswer()") == true, 20);
        test.Check("clangd rename: definition and call change together", () =>
            sourceDocument?.Editor.Text.Contains("return projectAnswer()") == true && headerDocument?.Editor.Text.Contains("int projectAnswer()") == true);
        test.Check("clangd rename: comments and unsaved edits survive", () =>
            sourceDocument?.Editor.Text.Contains("// answer in this comment must stay unchanged") == true);
        test.Check("clangd rename: neither file is saved implicitly", () =>
            File.ReadAllText(source).Contains("return answer()") && !File.ReadAllText(source).Contains("projectAnswer")
            && File.ReadAllText(header) == originalHeader);
        test.Run(() => { sourceDocument!.Editor.Document.Undo(); headerDocument!.Editor.Document.Undo(); });
        test.Check("clangd rename: undo restores each original buffer", () =>
            sourceDocument?.Editor.Text == originalSource && headerDocument?.Editor.Text == originalHeader);
    }
}
