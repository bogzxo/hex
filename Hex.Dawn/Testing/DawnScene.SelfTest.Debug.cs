using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of debugging C#. A little program of its own in the test's folder, a breakpoint on it, F5, and the
// program stopped there with the line lit, the stack and its variables on the Debug side, a step, and on to its end with
// what it wrote in the output. Only where netcoredbg is.
internal sealed partial class DawnScene
{
    private void TestDebug(HexPointerScript test)
    {
        if (debugger.Netcoredbg() is null)
        {
            test.Check("debug: netcoredbg is required to verify real debugging; install it or configure debugger_path", () => false);
            return;
        }

        string folder = Path.Combine(testFolder, "debugme");
        string program = Path.Combine(folder, "Program.cs");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "debugme.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(program, "int answer = 6 * 7;\nstring said = \"forty two\";\nSystem.Console.WriteLine(said);\nSystem.Console.WriteLine(answer + 1);\nint[] values = [2, 4, 6];\nanswer++;\nSystem.Console.WriteLine(answer);\n");
            Open(program);
        });
        test.Wait();
        test.Check("debug: a .cs file's editor takes breakpoints in its gutter", () => document.IsText && HexDebugging.IsCSharp(document.Path) && document.Editor.OnGutterPressed is not null);

        test.Run(() =>
        {
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(1, 0));
            debugger.ToggleBreakpointHere();
        });
        test.Check("debug: F9 puts one down on the caret's line", () => document.Editor.Breakpoints?.Contains(1) == true);
        test.Run(() => debugger.ToggleBreakpoint(Path.GetFullPath(program), 3));
        test.Run(() => debugger.ToggleBreakpoint(Path.GetFullPath(program), 3));
        test.Check("debug: and the same again takes it up", () => document.Editor.Breakpoints is { Count: 1 } lines && lines.Contains(1));

        test.Run(() =>
        {
            code.Document.MoveTo(new UITextPosition(0, 0));
            code.Document.Insert("// temporary edit before debugging\n");
            code.Document.Undo();
            code.Document.Redo();
            code.Document.Undo();
            code.Document.MoveTo(new UITextPosition(1, 0));
            code.Document.MoveLines(-1);
            code.Document.Undo();
            code.Document.Redo();
            code.Document.Undo();
        });
        test.Check("debug: edited and undone source retains the launch breakpoint", () => code.Breakpoints is { Count: 1 } edited && edited.Contains(1));

        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped, 90.0f);
        test.Settle(0.3f);
        test.Check("debug: F5 builds it and runs it to the breakpoint", () => debugger.DebugStopped && debugger.stoppedFrames.Count > 0 && debugger.stoppedFrames[debugger.stoppedFrame].Line == 1);
        test.Check("debug: the file is on screen with the line lit", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(program) && document.Editor.ExecutionLine == 1);

        UITreeItem? Section(string name) => sideTree.Items.FirstOrDefault(item => item.Text == name);
        test.WaitFor(() => Section("Variables")?.Children?.Any(row => row.Text == "answer = 42") == true, 10.0f);
        test.Check("debug: the Debug side has where it stopped and what its variables are", () =>
            Side == HexSide.Debug && Section("Call stack")?.Children is { Count: > 0 } && Section("Variables")?.Children?.Any(row => row.Text == "answer = 42") == true);

        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0 && debugger.stoppedFrames[debugger.stoppedFrame].Line == 2, 20.0f);
        test.Check("debug: F10 steps to the next line", () => debugger.DebugStopped && debugger.stoppedFrames[debugger.stoppedFrame].Line == 2 && document.Editor.ExecutionLine == 2);

        // Exercise the real expression pipeline and watch UI in the shipped runtime, not only a fake DAP.
        UITreeItem? Expression(string section, string expression) => Section(section)?.Children?.FirstOrDefault(row => row.Text.StartsWith(expression + " = ", StringComparison.Ordinal));
        void Evaluate(string expression, string expected)
        {
            test.Run(() => debugger.EvaluateIntoImmediate(expression));
            test.WaitFor(() => Expression("Immediate", expression)?.Text == expression + " = " + expected, 10f);
            test.Check("debug expressions: " + expression + " evaluates to " + expected, () => Expression("Immediate", expression)?.Text == expression + " = " + expected);
        }
        Evaluate("answer + 1", "43");
        Evaluate("said.Length", "9");
        test.Run(() => AllCommands().First(command => command.Text.EndsWith(Said("debugging.add_watch"), StringComparison.Ordinal)).Picked());
        test.WaitFor(() => textDialog is { IsOpen: true }, 2f);
        test.Run(() =>
        {
            if (textDialog is { IsOpen: true } asked)
                Descendants(asked.Body).OfType<TextBox>().First().Text = "answer";
        });
        test.Settle(0.25f); // The prompt's card takes 0.22 s to enter.
        test.Check("debug watch: the prompt contains the requested expression", () =>
            textDialog is { IsOpen: true } asked && Descendants(asked.Body).OfType<TextBox>().Any(box => box.Text == "answer"));
        test.Click(() =>
        {
            var add = textDialog!.Buttons.First(button => button.Label == Said("debugging.add_button"));
            return add.Module!.ToWorld(add.Bounds.Center);
        });
        test.WaitFor(() => textDialog is not { IsOpen: true }, 2f);
        test.Check("debug watch: pressing Add accepts the prompt", () => textDialog is not { IsOpen: true });
        test.WaitFor(() => Expression("Watch", "answer")?.Text == "answer = 42", 10f);
        test.Check("debug watch: Add Watch displays the selected frame's value", () => Expression("Watch", "answer")?.Text == "answer = 42");
        test.Run(() => debugger.ToggleBreakpoint(Path.GetFullPath(program), 6));
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0 && debugger.stoppedFrames[debugger.stoppedFrame].Line == 6, 20f);
        test.WaitFor(() => Expression("Watch", "answer")?.Text == "answer = 43", 10f);
        test.Check("debug watch: continuing refreshes the watch at the next breakpoint", () => Expression("Watch", "answer")?.Text == "answer = 43");
        Evaluate("values.Length", "3");
        test.Run(() => debugger.EvaluateIntoImmediate("values.Count"));
        test.WaitFor(() => Expression("Immediate", "values.Count")?.Detail?.Contains("Length", StringComparison.Ordinal) == true, 10f);
        test.Check("debug expressions: an invalid array member suggests Length", () =>
            Expression("Immediate", "values.Count") is { Text: "values.Count = ?", Detail: { } detail } && detail.Contains("Length", StringComparison.Ordinal));
        test.Run(() => debugger.EvaluateIntoImmediate("said.Substrng"));
        test.WaitFor(() => Expression("Immediate", "said.Substrng")?.Detail?.Contains("Substring", StringComparison.Ordinal) == true, 10f);
        test.Check("debug expressions: a misspelled string member suggests Substring", () =>
            Expression("Immediate", "said.Substrng") is { Text: "said.Substrng = ?", Detail: { } detail } && detail.Contains("Substring", StringComparison.Ordinal));
        test.Check("debug expressions: built-in string members survive publication", () =>
            HexDebugExpressions.BuiltInMembers("string") is { } members && members.Contains("Length") && members.Contains("Substring"));
        test.Check("debug expressions: built-in numeric members survive publication", () =>
            HexDebugExpressions.BuiltInMembers("int") is { } members && members.Contains("CompareTo") && members.Contains("ToString"));
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging, 20.0f);
        test.Check("debug: F5 goes on to its end, and nothing's lit any more", () => !debugger.Debugging && document.Editor.ExecutionLine == -1);
        test.Check("debug: what it wrote is in the output", () => said.Any(line => line.Text.Contains("forty two")) && said.Any(line => line.Text.Contains("43")));

        test.Run(() =>
        {
            debugger.breakpoints.Clear();
            CloseDocument(document);
        });
        test.Wait();
    }
}
