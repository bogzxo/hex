namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeDataTips(HexPointerScript test, string language, bool list = false)
    {
        string root = Path.Combine(testFolder, "data-tips-" + language);
        string file = Path.Combine(root, language == "python" ? "main.py" : language == "cpp" ? "main.cpp" : "Program.cs");
        string source = language == "python" ? "class Box:\n    value = 7\ndef main():\n    box = Box()\n    print(box.value)\n    box.value = 9\n    print(box.value)\nmain()\n"
            : language == "cpp" ? "#include <cstdio>\nstruct Box { int value; };\nint main() {\n    Box box{7};\n    std::printf(\"%d\\n\", box.value);\n    box.value = 9;\n    std::printf(\"%d\\n\", box.value);\n}\n"
            : "class Box { public int value = 7; }\nclass Program {\n  static void Main() {\n    var box = new Box();\n    System.Console.WriteLine(box.value);\n    box.value = 9;\n    System.Console.WriteLine(box.value);\n  }\n}\n";
        if (list) source = source.Replace("box = Box()", "box = [7]").Replace("box.value", "box[0]");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, 4);
            debugger.ToggleBreakpoint(file, 6);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 4, 60);
        debugger.TestNativeDataTips(test, RunOrPlay, list ? "0" : "value");
        test.Run(() => { debugger.StopDebugging(); debugger.breakpoints.Clear(); });
    }
}

internal sealed partial class HexDebugging
{
    internal void TestNativeDataTips(HexPointerScript test, Action resume, string member)
    {
        string? watched = null;
        bool Member(string value) => tipTree?.Items.FirstOrDefault()?.Children?.Any(row => row.Text == member + " = " + value) == true;
        bool WatchValue(string value) => watched is not null && WatchSection().Children?.Any(row => row.Text == watched + " = " + value) == true;
        test.WaitFor(() =>
        {
            var doc = context.Documents.Current;
            int column = doc.Editor.Document.LineAt(3).IndexOf("box", StringComparison.Ordinal);
            DataTip(doc, doc.Editor, new(3, column + 1));
            if (tipWindow is null) return false;
            tipPinned = true;
            return true;
        }, 5);
        test.Check("Data tips: hovering a compound local opens an expandable tip", () => tipWindow is not null && tipTree?.Items.FirstOrDefault()?.Expandable == true);
        test.WaitFor(() => Member("7"), 5);
        test.Check("Data tips: native members appear inside the tip", () => Member("7"));
        test.Run(() =>
        {
            var row = tipTree?.Items.FirstOrDefault()?.Children?.FirstOrDefault(row => row.Text == member + " = 7");
            if (row is null) return;
            watched = TipPathOf(row);
            Watch(watched);
        });
        test.WaitFor(() => WatchValue("7"), 5);
        test.Check("Data tips: adding a child to Watch evaluates its native expression", () => WatchValue("7"));
        test.Check("Data tips: child Watch is persisted with the workspace", () => watched is not null && context.Workspace.WatchExpressions.Contains(watched));
        test.Run(resume);
        test.WaitFor(() => DebugStopped && context.Documents.Current.Editor.ExecutionLine == 6, 15);
        test.WaitFor(() => Member("9"), 5);
        test.Check("Data tips: pinned members refresh at the next breakpoint", () => Member("9"));
        test.WaitFor(() => WatchValue("9"), 5);
        test.Check("Data tips: child watch follows the value at the next breakpoint", () => WatchValue("9"));
        test.Run(() => { CloseTip(); if (watched is not null) RemoveWatch(watched); });
    }
}
