using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestPythonExplorer(HexPointerScript script, string folder, Action<string> open, bool nested = false)
    {
        string projectRoot = folder;
        folder = Path.Combine(projectRoot, "tests");
        string file = Path.Combine(nested ? Path.Combine(folder, "unit") : folder, "test_sample.py");
        string testClass = (nested ? "unit." : "") + "test_sample.Sample.";
        script.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (nested) File.WriteAllText(Path.Combine(folder, "unit", "__init__.py"), "");
            File.WriteAllText(Path.Combine(projectRoot, "application.py"), "answer = 42\n");
            File.WriteAllText(Path.Combine(projectRoot, "fixture.txt"), "project data");
            File.WriteAllText(file, "import unittest, pathlib; from application import answer\nclass Sample(unittest.TestCase):\n    def test_pass(self):\n        self.assertEqual(answer, 42); self.assertEqual(pathlib.Path('fixture.txt').read_text(), 'project data')\n    def test_fail(self): self.assertEqual(1, 2)\n    @unittest.skip('not today')\n    def test_skip(self): pass\n");
            open(projectRoot);
            context.Documents.OpenAt(file, 2, 0);
            RunHere();
        });
        script.WaitFor(() => discovered && !discovering, 30f);
        script.Check("Python tests: discovers three unittest methods", () => tests.GetValueOrDefault(folder)?.Count == 3);
        script.WaitFor(() => !running, 20f);
        script.Check("Python tests: first Run Here waits for discovery and runs one test", () => results.Count == 1 && results.ContainsKey(Identity(folder, testClass + "test_pass")));
        script.Run(() => Run(folder, null, "Python suite"));
        script.WaitFor(() => !running, 30f);
        script.Check("Python tests: reports pass, fail and skip", () => tests.TryGetValue(folder, out var found)
            && found.Select(Outcome).Order().SequenceEqual(new[] { DotnetTestOutcome.Passed, DotnetTestOutcome.Failed, DotnetTestOutcome.Skipped }.Order()));
        script.Check("Python tests: failure has source location in Problems", () => failures.Count == 1 && PlatformPaths.Same(failures[0].Path, file) && failures[0].Line == 4);
        script.Run(() => GoTo(tests[folder].Single(test => test.Method == "test_pass")));
        script.Check("Python tests: source navigation opens the method", () => PlatformPaths.Same(context.Documents.Current.Path!, file) && context.Documents.Current.Editor.Document.Caret.Line == 2);
        script.Run(() => Run(folder, [testClass + "test_pass"], "selected Python test"));
        script.WaitFor(() => !running, 30f);
        script.Check("Python tests: selected rerun preserves other results", () => failures.Count == 1 && ResultsOf(tests[folder].Single(test => test.Method == "test_pass")).Single().Outcome == DotnetTestOutcome.Passed);
        script.Run(() =>
        {
            results.Clear();
            pressedLast = null;
            context.Documents.Current.Editor.Document.MoveTo(new Horizon.UI.Components.UITextPosition(2, 0));
            RunHere();
        });
        script.WaitFor(() => !running, 20f);
        script.Check("Python tests: Run Here executes only the caret test", () => results.Count == 1
            && results.ContainsKey(Identity(folder, testClass + "test_pass")) && !anyFailed);
        script.Run(() =>
        {
            debugger.ToggleBreakpoint(file, 3);
            context.Documents.Current.Editor.Document.MoveTo(new Horizon.UI.Components.UITextPosition(2, 0));
            DebugHere();
        });
        script.WaitFor(() => debugger.DebugStopped, 20f);
        script.Check("Python tests: Debug Here stops in the selected test", () => debugger.DebugStopped
            && debugger.stoppedFrames.Any(frame => frame.File is not null && PlatformPaths.Same(frame.File, file) && frame.Line == 3 && frame.Name == "test_pass"));
        script.Run(debugger.StopDebugging);
        script.WaitFor(() => !debugger.Debugging, 10f);
        script.Check("Python tests: stopping debug clears pending state without a result", () => !running && waiting.Count == 0 && !runningIn.Contains(folder) && results.Count == 0);
        script.Run(() => debugger.breakpoints.Clear());
        script.Run(() => Debug(folder, null, "Debug Python suite"));
        script.WaitFor(() => !debugger.Debugging && !running, 25f);
        script.Check("Python tests: debugging reports pass, fail and skip", () => tests[folder].Select(Outcome).Order()
            .SequenceEqual(new[] { DotnetTestOutcome.Passed, DotnetTestOutcome.Failed, DotnetTestOutcome.Skipped }.Order()));
        script.Check("Python tests: debug failure has its traceback and source location", () => ResultsOf(tests[folder].Single(test => test.Method == "test_fail")).SingleOrDefault()
            is { At: { } at } failed && PlatformPaths.Same(at.File, file) && at.Line == 4 && failed.Stack.Contains("AssertionError"));
        script.Run(() => Debug(folder, [testClass + "test_pass"], "Debug selected Python test"));
        script.WaitFor(() => !debugger.Debugging && !running, 25f);
        script.Check("Python tests: selected debug preserves other outcomes", () => results.Count == 3 && failures.Count == 1
            && Outcome(tests[folder].Single(test => test.Method == "test_pass")) == DotnetTestOutcome.Passed);
        script.Run(() =>
        {
            debugger.ToggleBreakpoint(file, 3);
            Debug(folder, null, "Debug partial Python suite");
        });
        script.WaitFor(() => debugger.DebugStopped, 25f);
        script.Check("Python tests: partial suite stops inside the running test", () => debugger.stoppedFrames.FirstOrDefault().Name == "test_pass");
        script.Run(Cancel);
        script.WaitFor(() => !debugger.Debugging && !running, 10f);
        script.Check("Python tests: Cancel keeps completed results but not the interrupted test", () =>
        {
            return results.Count == 1 && Outcome(tests[folder].Single(test => test.Method == "test_fail")) == DotnetTestOutcome.Failed
                && !ResultsOf(tests[folder].Single(test => test.Method == "test_pass")).Any() && waiting.Count == 0;
        });
        script.Run(() => debugger.breakpoints.Clear());
    }
}
