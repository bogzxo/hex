namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestWatchValue(HexPointerScript test, string language)
    {
        bool python = language == "python", cpp = language == "cpp";
        string root = Path.Combine(testFolder, "watch value " + language);
        string file = Path.Combine(root, python ? "main.py" : cpp ? "main.cpp" : "Program.cs");
        string expression = python || cpp ? "box.value" : "box.Value";
        int entry = cpp ? 4 : 3;
        string source = python
            ? "class Box:\n    def __init__(self): self.value = 42\nbox = Box()\nprint(box.value)\nbox.value += 1\nprint(box.value)\n"
            : cpp ? "#include <cstdio>\nstruct Box { int value; };\nint main() {\n Box box{42};\n std::printf(\"%d\\n\", box.value);\n box.value++;\n std::printf(\"%d\\n\", box.value);\n}\n"
            : "class Box { public int Value = 42; }\nclass Program { static void Main() {\n var box = new Box();\n System.Console.WriteLine(box.Value);\n box.Value++;\n System.Console.WriteLine(box.Value);\n} }\n";
        UITreeItem? Value() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.watch"))?.Children?
            .FirstOrDefault(row => row.Text.StartsWith(expression + " = ", StringComparison.Ordinal));
        void Menu()
        {
            if (Value() is {} row) sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
        }
        void Edit()
        {
            Menu();
            var edit = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.edit_value"))?.OnPicked;
            contextMenu?.Close();
            edit?.Invoke();
        }
        void Submit(string value)
        {
            if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(value);
        }
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
        test.Run(() => debugger.WatchExpression(expression));
        test.WaitFor(() => Value()?.Text == expression + " = 42", 10f);
        test.Run(Menu);
        test.Check("Watch value: " + language + " edit action follows native capability", () => Value()?.Text == expression + " = 42"
            && (contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.edit_value")) == true) == !cpp);
        test.Run(() => contextMenu?.Close());
        if (cpp)
        {
            test.Run(() => { debugger.StopDebugging(); debugger.breakpoints.Clear(); });
            test.WaitFor(() => !debugger.Debugging, 10f);
            return;
        }
        test.Run(Edit);
        test.Check("Watch value: " + language + " prompt shows the native value", () => textDialog is { IsOpen: true } dialog
            && Descendants(dialog.Body).OfType<TextBox>().First().Text == "42");
        int beforeError = 0;
        test.Run(() => { beforeError = said.Count; Submit("not_a_valid_variable_name"); });
        test.WaitFor(() => !debugger.EditingVariable && said.Skip(beforeError).Any(entry => entry.Error), 10f);
        test.Check("Watch value: " + language + " invalid assignment explains failure and preserves the value", () =>
            Value()?.Text == expression + " = 42" && said.Skip(beforeError).Any(entry => entry.Error));
        test.Run(Edit);
        test.Run(() => Submit("100"));
        test.WaitFor(() => Value()?.Text == expression + " = 100", 10f);
        test.Check("Watch value: " + language + " native assignment refreshes the watch", () => Value()?.Text == expression + " = 100");
        test.Run(Edit);
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry + 1, 10f);
        test.Run(() => Submit("999"));
        test.WaitFor(() => Value()?.Text == expression + " = 100", 10f);
        test.Check("Watch value: " + language + " earlier-stop prompt cannot change the next stop", () =>
            debugger.DebugStopped && code.ExecutionLine == entry + 1 && Value()?.Text == expression + " = 100");
        test.Check("Watch value: " + language + " value editing preserves the expression and source", () =>
            explorer.workspace.Watches.SequenceEqual(new[] { expression }) && code.Text == source && File.ReadAllText(file) == source);
        int beforeContinue = 0;
        test.Run(() => { beforeContinue = said.Count; RunOrPlay(); });
        test.WaitFor(() => !debugger.Debugging, 15f);
        test.Check("Watch value: " + language + " program continues with the assigned value", () => !debugger.Debugging
            && said.Skip(beforeContinue).Any(entry => entry.Text.Split('\n').Any(line => line.Trim() == "101")));
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
    }
}
