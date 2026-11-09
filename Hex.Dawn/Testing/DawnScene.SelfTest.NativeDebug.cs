namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeDebug(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "native-debug");
        string file = Path.Combine(folder, "main.cpp");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "#include <cstdio>\nint main() {\n    int answer = 42;\n    std::printf(\"%d\\n\", answer);\n    answer++;\n    return answer - 43;\n}\n");
            Open(file);
        });
        test.Wait();
        test.Check("C++ debug: gutter supports breakpoints", () => document.Editor.OnGutterPressed is not null);
        test.Run(() => { debugger.ToggleBreakpoint(file, 3); RunOrPlay(); });
        test.WaitFor(() => debugger.DebugStopped, 30f);
        test.Check("C++ debug: F5 compiles and stops on the source line", () => debugger.DebugStopped && document.Editor.ExecutionLine == 3);
        bool HasAnswer() => sideTree.Items.Any(section => section.Text == "Variables" && section.Children?.Any(row => row.Text == "answer = 42") == true);
        test.WaitFor(HasAnswer, 10f);
        test.Check("C++ debug: locals show answer 42", HasAnswer);
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 4, 10f);
        test.Check("C++ debug: Step Over advances one line", () => debugger.DebugStopped && document.Editor.ExecutionLine == 4);
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("C++ debug: Stop ends the session", () => !debugger.Debugging);
        test.Run(() => { debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
