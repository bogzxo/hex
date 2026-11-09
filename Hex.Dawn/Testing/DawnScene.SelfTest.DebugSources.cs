using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebuggerSources(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "debugger source"), main = Path.Combine(root, "main.py"), generated = Path.Combine(root, "generated.py"), notes = Path.Combine(root, "notes.txt");
        string snippet = "answer = 41\nimport debugpy\ndebugpy.breakpoint()\nanswer += 1\nprint(answer)\n";
        string program = "import linecache\ntext = " + JsonValue.Create(snippet)!.ToJsonString() + "\nname = "
            + JsonValue.Create(generated)!.ToJsonString() + "\nlinecache.cache[name] = (len(text), None, text.splitlines(keepends=True), name)\nexec(compile(text, name, 'exec'))\n";
        string firstSnapshot = "", secondSnapshot = "";
        int reference = 0;
        bool Has(string key) => PaletteChoices().Any(choice => choice.Text == Said(key));
        void Command(string key) => PaletteChoices().FirstOrDefault(choice => choice.Text == Said(key))?.Picked?.Invoke();
        bool InSource(int line) => debugger.DebugStopped && HexDebugging.IsDebuggerSourcePath(document.Path) && code.ReadOnly && code.ExecutionLine == line && code.Text == snippet;
        bool Locals(string value) => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?.Any(row => row.Text == "answer = " + value) == true;
        bool Watches(string value) => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.watch"))?.Children?.Any(row => row.Text == "answer + 1 = " + value) == true;
        bool SamePath(string? path, string expected) => path is {} actual && PlatformPaths.Same(actual, expected);
        void PickFrame(string file)
        {
            var section = sideTree.Items.FirstOrDefault(row => row.Text == Said("debugging.call_stack"));
            var row = section?.Children?.FirstOrDefault(row => row.Detail?.StartsWith(file + ":", StringComparison.Ordinal) == true);
            if (row?.Tag is SideTag { Pressed: {} pressed }) pressed();
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(main, program);
            File.WriteAllText(notes, "debugger source navigation notes\n");
            explorer.OpenFolder(root);
            Open(main);
            debugger.breakpoints.Clear();
            RunOrPlay();
        });
        test.WaitFor(() => InSource(3) && Locals("41"), 45);
        test.Run(() => { if (!InSource(3)) foreach (var entry in said.TakeLast(15)) Console.WriteLine("[Debugger source probe] " + entry.Text); });
        test.Check("Debugger source: a generated Python frame opens instead of the outer local frame", () => InSource(3) && debugger.stoppedFrames[0].Source is { Reference: > 0 }
            && !File.Exists(generated) && Locals("41"));
        test.Run(() => { firstSnapshot = document.Path ?? ""; reference = debugger.stoppedFrames.FirstOrDefault().Source?.Reference ?? 0; });
        test.Check("Debugger source: snapshot has Python syntax and no language-server document", () => HexDebugging.IsDebuggerSourcePath(document.Path)
            && ReferenceEquals(code.Highlighter, CodeLanguages.ForFile("generated.py")) && languages.LinterOf(document) is null);
        test.Check("Debugger source: session breakpoint bindings do not enable file-only actions", () => code.OnGutterPressed is not null
            && Has("debugging.toggle_breakpoint") && !Has("debugging.run_to_cursor") && !Has("debugging.set_next_statement")
            && debugger.breakpoints.Keys.All(path => !HexDebugging.IsDebuggerSourcePath(path)));
        test.Run(() =>
        {
            typeof(CodeEditor).GetMethod("OnTextInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(code, ['x']);
            SaveFormatted();
        });
        test.Check("Debugger source: editing and save cannot alter native text or the program", () => InSource(3) && !document.Modified && !document.CodeEdited
            && File.ReadAllText(firstSnapshot) == snippet && File.ReadAllText(main) == program && !File.Exists(generated));
        test.Run(() => debugger.WatchExpression("answer + 1"));
        test.WaitFor(() => Watches("42"), 10);
        test.Check("Debugger source: Watch evaluates in the downloaded frame", () => Watches("42"));
        test.Run(() => PickFrame("main.py"));
        test.WaitFor(() => SamePath(document.Path, main) && code.ExecutionLine == 4, 10);
        test.Check("Debugger source: selecting the outer frame opens its real file", () => SamePath(document.Path, main) && code.ExecutionLine == 4);
        test.Run(() => PickFrame("generated.py"));
        test.WaitFor(() => InSource(3), 10);
        test.Check("Debugger source: selecting the generated frame reuses its snapshot tab", () => InSource(3) && SamePath(document.Path, firstSnapshot)
            && documents.Count(open => HexDebugging.IsDebuggerSourcePath(open.Path)) == 1);
        test.Run(() =>
        {
            File.WriteAllText(generated, "wrong disk content\n");
            Open(notes);
            Command("debugging.show_current_frame");
        });
        test.WaitFor(() => InSource(3), 10);
        test.Check("Debugger source: positive native reference takes precedence over an existing disk file", () => InSource(3)
            && SamePath(document.Path, firstSnapshot) && File.ReadAllText(generated) == "wrong disk content\n");
        test.Run(() => { File.Delete(generated); Command("debugging.show_current_frame"); Open(notes); });
        test.Settle(0.5f);
        test.Check("Debugger source: a late source response cannot take over another document", () => SamePath(document.Path, notes));
        test.Run(() => { Command("debugging.show_current_frame"); Command("debugging.add_watch"); });
        test.Settle(0.5f);
        test.Check("Debugger source: a pending source cannot take focus from a new input dialog", () => SamePath(document.Path, notes)
            && textDialog is { IsOpen: true } && compositor.Focus is TextBox);
        PressKeys(test, "Escape");
        test.Run(() => Command("debugging.show_current_frame"));
        test.WaitFor(() => InSource(3), 10);
        test.Run(() =>
        {
            Command("debugging.show_current_frame");
            code.Document.MoveTo(new(0, 0));
        });
        test.Settle(0.5f);
        test.Check("Debugger source: a late response respects caret navigation", () => code.Document.Caret.Line == 0);
        test.Run(() => Command("debugging.show_current_frame"));
        test.WaitFor(() => InSource(3) && code.Document.Caret.Line == 3, 10);
        test.Run(debugger.StepOver);
        test.WaitFor(() => InSource(4) && Locals("42") && Watches("43"), 15);
        test.Check("Debugger source: stepping advances the marker and updates native locals and watches", () => InSource(4) && Locals("42") && Watches("43"));
        test.Check("Debugger source: changing native references do not create another tab for identical source", () => SamePath(document.Path, firstSnapshot)
            && documents.Count(open => HexDebugging.IsDebuggerSourcePath(open.Path)) == 1);
        test.Run(debugger.RestartDebugging);
        test.WaitFor(() => InSource(3) && Locals("41"), 45);
        test.Run(() => secondSnapshot = document.Path ?? "");
        test.Check("Debugger source: a reused native reference belongs to a fresh session snapshot", () => InSource(3)
            && debugger.stoppedFrames[0].Source?.Reference == reference && !PlatformPaths.Same(firstSnapshot, secondSnapshot));
        test.Check("Debugger source: the old session's snapshot has no execution marker", () => documents.FirstOrDefault(open => SamePath(open.Path, firstSnapshot))?.Editor.ExecutionLine == -1);
        test.Run(() =>
        {
            Command("debugging.show_current_frame");
            string replacement = Path.Combine(testFolder, "debugger source replacement");
            Directory.CreateDirectory(replacement);
            explorer.OpenFolder(replacement);
            Open(notes);
        });
        test.Settle(0.5f);
        test.Check("Debugger source: workspace replacement rejects a pending source request", () => !debugger.Debugging && SamePath(document.Path, notes)
            && documents.All(open => open.Editor.ExecutionLine < 0));
        test.Run(() => { CloseDocument(documents.First(open => SamePath(open.Path, secondSnapshot))); Open(secondSnapshot); });
        test.Check("Debugger source: reopening an ended snapshot remains read-only and cannot run as a program", () => code.ReadOnly && HexDebugging.IsDebuggerSourcePath(document.Path)
            && !Runnable(document) && !Has("debugging.start_debugging") && code.OnGutterPressed is null);
        test.Run(() => { RunOrPlay(); RunWithoutDebugging(); });
        test.Check("Debugger source: stale snapshots do not launch a new debug session or setup dialog", () => !debugger.Debugging && textDialog is not { IsOpen: true });
        test.Check("Debugger source: original program and generated-source path remain untouched", () => File.ReadAllText(main) == program && !File.Exists(generated)
            && File.ReadAllText(firstSnapshot) == snippet && File.ReadAllText(secondSnapshot) == snippet);
        test.Run(() => debugger.breakpoints.Clear());
    }
}
