namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebugCompletion(HexPointerScript test, string language)
    {
        bool python = language == "python", cpp = language == "cpp";
        string root = Path.Combine(testFolder, "debug completion " + language);
        string file = Path.Combine(root, python ? "main.py" : cpp ? "main.cpp" : "Program.cs");
        string source = python ? "class Box:\n    def __init__(self): self.value = 42\nbox = Box()\nprint(box.value)\nbox.value += 1\nprint(box.value)\n"
            : cpp ? "#include <cstdio>\nstruct Box { int value; };\nint main() {\n Box box{42};\n std::printf(\"%d\\n\", box.value);\n box.value++;\n std::printf(\"%d\\n\", box.value);\n}\n"
            : "class Box { public int Value = 42; }\nclass Program { static void Main() {\n var box = new Box();\n System.Console.WriteLine(box.Value);\n box.Value++;\n} }\n";
        int entry = cpp ? 4 : 3;
        CodeEditor? Input() => textDialog is { IsOpen: true } dialog ? Descendants(dialog.Body).OfType<CodeEditor>().FirstOrDefault() : null;
        void Prompt() => AllCommands().First(command => command.Text.Contains(Said("debugging.evaluate") + "   (", StringComparison.Ordinal)).Picked();
        bool Result(string expression, string value) => sideTree.Items.Any(section => section.Text == Said("debugging.immediate")
            && section.Children?.Any(row => row.Text == expression + " = " + value) == true);
        bool Member(UICompletion item) => item.Text is "value" or "box.value";
        void Type(CodeEditor input, char character) => typeof(CodeEditor).GetMethod("OnTextInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(input, [character]);
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry, 60f);
        test.Check("Debug completion: " + language + " native breakpoint is reached", () => debugger.DebugStopped && code.ExecutionLine == entry);
        test.Run(Prompt);
        test.Check("Debug completion: " + language + " expression input follows native capability", () => (Input() is not null) == (python || cpp));
        if (!python && !cpp)
        {
            test.Run(() =>
            {
                if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke("box.Value");
            });
            test.WaitFor(() => Result("box.Value", "42"), 10f);
            test.Check("Debug completion: unsupported adapters retain expression evaluation", () => Result("box.Value", "42"));
        }
        else
        {
            bool retired = false;
            test.Run(() =>
            {
                if (Input() is not {} input) return;
                input.Text = "box.v + 1";
                input.Document.MoveTo(new(0, 5));
                Type(input, 'a');
            });
            test.WaitFor(() => Input()?.Completions.Any(Member) == true, 10f);
            test.Check("Debug completion: " + language + " native member suggestions arrive", () => Input()?.Completions.Any(Member) == true);
            test.Run(() =>
            {
                if (Input() is not {} input) return;
                int index = input.Completions.ToList().FindIndex(item => Member(item));
                if (index >= 0) { input.MoveCompletion(index); Type(input, UIKeyboard.ENTER); }
            });
            test.Check("Debug completion: " + language + " Enter accepts the member and preserves the expression suffix", () => Input()?.Text == "box.value + 1");
            test.Run(() => Input()?.Document.Undo());
            test.Check("Debug completion: " + language + " acceptance is one undo step", () => Input()?.Text == "box.va + 1");
            test.Run(() => { if (Input() is {} input) { input.Document.Redo(); Type(input, UIKeyboard.ENTER); } });
            test.WaitFor(() => Result("box.value + 1", "43"), 10f);
            test.Check("Debug completion: " + language + " Enter evaluates the completed expression in its paused frame", () => textDialog?.IsOpen != true && Result("box.value + 1", "43"));
            test.Run(Prompt);
            test.Run(() =>
            {
                if (Input() is not {} input) return;
                input.Text = "box.va";
                input.Document.MoveTo(new(0, 6));
                input.Complete();
            });
            test.WaitFor(() => Input()?.Completions.Any(Member) == true, 10f);
            test.Run(debugger.StepOver);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry + 1, 10f);
            test.Run(() =>
            {
                if (Input() is {} input)
                {
                    input.AcceptCompletion();
                    retired = input.Text == "box.va" && !input.CompletionOpen;
                    Type(input, UIKeyboard.ENTER);
                }
            });
            test.Check("Debug completion: " + language + " an earlier-stop completion cannot change the prompt", () => retired);
            test.Check("Debug completion: " + language + " a prompt from an earlier stop cannot evaluate in the next frame", () =>
                !sideTree.Items.Any(section => section.Text == Said("debugging.immediate") && section.Children?.Any(row =>
                    row.Text == "box.va" || row.Text.StartsWith("box.va = ", StringComparison.Ordinal) || row.Text.StartsWith("box.value = ", StringComparison.Ordinal)) == true));
        }
        test.Check("Debug completion: " + language + " evaluation preserves source and breakpoints", () => code.Text == source
            && File.ReadAllText(file) == source && debugger.breakpoints[file].SetEquals([entry]));
        test.Run(() => { textDialog?.Close(); if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
        test.WaitFor(() => !debugger.Debugging, 10f);
    }
}
