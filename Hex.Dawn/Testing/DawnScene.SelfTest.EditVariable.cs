using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestEditVariable(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "edit-variable-" + language);
        string file = Path.Combine(root, language == "cpp" ? "main.cpp" : language == "python" ? "main.py" : "Program.cs");
        int entry = language == "cpp" ? 3 : 1;
        UITreeItem? Value() => sideTree.Items.FirstOrDefault(section => section.Text == "Variables")?.Children?.FirstOrDefault(row => row.Text.StartsWith("value = ", StringComparison.Ordinal));
        void Submit(string value)
        {
            if (textDialog is { IsOpen: true } dialog) Descendants(dialog.Body).OfType<TextBox>().First().OnSubmitted?.Invoke(value);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, language == "cpp"
                ? "#include <cstdio>\nint main() {\n int value = 42;\n std::printf(\"%d\\n\", value);\n value++;\n std::printf(\"%d\\n\", value);\n}\n"
                : language == "python" ? "value = 42\nprint(value)\nvalue += 1\nprint(value)\n"
                : "int value = 42;\nSystem.Console.WriteLine(value);\nvalue++;\nSystem.Console.WriteLine(value);\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && Value()?.Text == "value = 42", 45f);
        test.Check("Edit value: native local is editable", () => debugger.DebugStopped && Value()?.Tag is SideTag { Menu: not null });
        test.Run(() => (Value()?.Tag as SideTag)?.Menu?.Invoke(default));
        test.Check("Edit value: row opens value prompt", () => textDialog is { IsOpen: true });
        int beforeError = 0;
        UITreeItem? beforeEdit = null;
        test.Run(() => { beforeError = said.Count; beforeEdit = Value(); Submit("not_a_valid_variable_name"); });
        test.WaitFor(() => !debugger.EditingVariable && Value()?.Text == "value = 42" && (language != "python" || !ReferenceEquals(beforeEdit, Value())), 10f);
        test.Check("Edit value: invalid expression preserves original local", () => Value()?.Text == "value = 42"
            && (language == "python" || said.Skip(beforeError).Any(entry => entry.Text.Contains("Could not change the value", StringComparison.Ordinal))));
        test.Run(() => (Value()?.Tag as SideTag)?.Menu?.Invoke(default));
        test.Run(() => Submit("100"));
        test.WaitFor(() => Value()?.Text == "value = 100", 10f);
        test.Check("Edit value: accepted change refreshes locals", () => Value()?.Text == "value = 100");
        test.Run(() => (Value()?.Tag as SideTag)?.Menu?.Invoke(default));
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == entry + 1, 10f);
        test.Run(() => Submit("999"));
        test.WaitFor(() => Value()?.Text == "value = 100", 10f);
        test.Check("Edit value: a prompt from an earlier stop cannot change the next stop", () => debugger.DebugStopped
            && document.Editor.ExecutionLine == entry + 1 && Value()?.Text == "value = 100");
        test.Run(RunOrPlay);
        // LLDB's redirected program output can be drained after the session ends.
        test.WaitFor(() => !debugger.Debugging && said.Any(entry => entry.Text.Split('\n').Any(line => line.Trim() == "101")), 15f);
        test.Check("Edit value: program continues with the changed value", () => !debugger.Debugging && said.Any(entry => entry.Text.Split('\n').Any(line => line.Trim() == "101")));
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
