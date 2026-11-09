namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestFrameValues(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "frame values"), file = Path.Combine(root, "Program.cs");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, "class Program {\n  static void Worker() {\n    int answer = 7;\n    System.Console.WriteLine(answer);\n  }\n  static void Main() {\n    int answer = 42;\n    Worker();\n    System.Console.WriteLine(answer);\n  }\n}\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, 3);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count >= 2, 60);
        debugger.TestFrameValues(test);
        test.Run(() => { debugger.StopDebugging(); debugger.breakpoints.Clear(); });
    }
}

internal sealed partial class HexDebugging
{
    internal void TestFrameValues(HexPointerScript test)
    {
        int worker = -1, caller = -1;
        bool Tip(string value) => tipTree?.Items.FirstOrDefault()?.Text == "answer = " + value;
        bool Watched(string value) => WatchSection().Children?.Any(row => row.Text == "answer = " + value) == true;
        test.Run(() =>
        {
            worker = stoppedFrames.ToList().FindIndex(frame => frame.Name.Contains("Worker", StringComparison.Ordinal));
            caller = stoppedFrames.ToList().FindIndex(frame => frame.Name.Contains("Main", StringComparison.Ordinal));
            if (debugging is not { } session || worker < 0 || caller < 0) return;
            var editor = context.Documents.Current.Editor;
            OpenTip(session, editor, new DebugVariable("answer", "?", "int", 0), editor.PlaceOf(new(3, 4)), editor.PlaceOf(new(3, 10)));
            tipPinned = true;
            RefreshPinnedTip();
            Watch("answer");
        });
        test.WaitFor(() => Tip("7"), 10);
        test.Check("Frame values: pinned local shows the worker value", () => worker >= 0 && caller >= 0 && Tip("7"));
        test.Run(() => { if (caller >= 0) (FrameItem(caller).Tag as SideTag)?.Pressed?.Invoke(); });
        test.WaitFor(() => Tip("42"), 5);
        test.Check("Frame values: selecting the caller refreshes the pinned local", () => stoppedFrame == caller && Tip("42"));
        test.WaitFor(() => Watched("42"), 5);
        test.Check("Frame values: watch agrees with the caller's pinned local", () => Watched("42"));
        test.Run(() => { if (worker >= 0) (FrameItem(worker).Tag as SideTag)?.Pressed?.Invoke(); });
        test.WaitFor(() => Tip("7"), 5);
        test.Check("Frame values: selecting the worker restores its pinned local", () => stoppedFrame == worker && Tip("7"));
        test.WaitFor(() => Watched("7"), 5);
        test.Check("Frame values: watch agrees with the worker's pinned local", () => Watched("7"));
        test.Run(() =>
        {
            tipPinned = false;
            if (caller >= 0) (FrameItem(caller).Tag as SideTag)?.Pressed?.Invoke();
        });
        test.Check("Frame values: switching frames closes an unpinned tip", () => tipWindow is null);
        test.Run(() => { CloseTip(); RemoveWatch("answer"); });
    }
}
