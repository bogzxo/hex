using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestPytestExplorer(HexPointerScript script, string root, Action<string> open)
    {
        string file = Path.Combine(root, "checks", "test_sample.py");
        const string passed = "checks/test_sample.py::test_number[zero.one(0)]";
        Task? setup = null;
        script.Run(() => setup = Task.Run(async () =>
        {
            string python = Environment.GetEnvironmentVariable("HEX_TEST_PYTEST") ?? throw new IOException("HEX_TEST_PYTEST must name a Python environment containing pytest.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var packages = await CMakeProject.Run(python, root, ["-c", "import sysconfig; print(sysconfig.get_path('purelib'))"], CancellationToken.None);
            if (packages.ExitCode != 0) throw new IOException(packages.Output);
            var created = await CMakeProject.Run(python, root, ["-m", "venv", "--without-pip", Path.Combine(root, ".venv")], CancellationToken.None);
            if (created.ExitCode != 0) throw new IOException(created.Output);
            string local = PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv"));
            var linked = await CMakeProject.Run(local, root, ["-c", "import pathlib,sysconfig,sys; pathlib.Path(sysconfig.get_path('purelib'), 'fixture_pytest.pth').write_text(sys.argv[1]+'\\n')", packages.Output.Trim()], CancellationToken.None);
            if (linked.ExitCode != 0) throw new IOException(linked.Output);
            File.WriteAllText(Path.Combine(root, "pytest.ini"), "[pytest]\ntestpaths = checks\n");
            File.WriteAllText(file, "import pytest\n@pytest.fixture\ndef answer(): return 42\n@pytest.mark.parametrize('number', [0, 1], ids=['zero.one(0)', 'one.case(1)'])\ndef test_number(number, answer):\n    assert number == 0 and answer == 42\n@pytest.mark.skip(reason='not today')\ndef test_skip(): pass\n");
        }));
        script.WaitFor(() => setup?.IsCompleted == true, 30);
        script.Check("pytest: project-local interpreter is prepared", () => setup?.IsCompletedSuccessfully == true);
        script.Run(() => { open(root); context.Documents.OpenAt(file, 4, 0); RunHere(); });
        script.WaitFor(() => discovered && !discovering, 30);
        script.Check("pytest: native discovery lists each parameterized case and skip", () => tests.GetValueOrDefault(root)?.Count == 3
            && tests[root].Any(test => test.FullName == passed));
        script.WaitFor(() => !running, 30);
        script.Check("pytest: Run Here executes both cases of the source method", () => results.Count == 2
            && results.ContainsKey(Identity(root, passed)) && failures.Count == 1);
        script.Run(() => Run(root, null, "pytest suite"));
        script.WaitFor(() => !running, 30);
        script.Check("pytest: full run reports pass, failure and skip", () => tests.TryGetValue(root, out var found)
            && found.Select(Outcome).Order().SequenceEqual(new[] { DotnetTestOutcome.Passed, DotnetTestOutcome.Failed, DotnetTestOutcome.Skipped }.Order()));
        script.Check("pytest: assertion failure navigates to its source", () => failures.Count == 1 && PlatformPaths.Same(failures[0].Path, file) && failures[0].Line == 5);
        script.Run(() => GoTo(tests[root].Single(test => test.FullName == passed)));
        script.Check("pytest: discovered source navigation reaches the test", () => PlatformPaths.Same(context.Documents.Current.Path!, file)
            && context.Documents.Current.Editor.Document.Caret.Line == tests[root].Single(test => test.FullName == passed).Line);
        script.Run(() => Run(root, [passed], "selected pytest case"));
        script.WaitFor(() => !running, 30);
        script.Check("pytest: selected rerun preserves other parameter and skip results", () => results.Count == 3 && failures.Count == 1
            && Outcome(tests[root].Single(test => test.FullName == passed)) == DotnetTestOutcome.Passed);
        script.Run(() =>
        {
            File.Delete(Path.Combine(root, "pytest.ini"));
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = 'plain-tests'\nversion = '1.0.0'\n");
            Discover();
        });
        script.WaitFor(() => !discovering, 30);
        script.Check("pytest: removing framework configuration retains native discovery", () => tests.GetValueOrDefault(root)?.Count == 3
            && tests[root].Any(test => test.FullName == passed));
        script.Run(() => { debugger.ToggleBreakpoint(file, 5); Debug(root, [passed], "debug pytest case"); });
        script.WaitFor(() => debugger.DebugStopped, 30);
        script.Check("pytest: selected case stops at its source breakpoint", () => debugger.DebugStopped
            && debugger.stoppedFrames.Any(frame => frame.File is not null && PlatformPaths.Same(frame.File, file) && frame.Line == 5 && frame.Name == "test_number"));
        script.Run(debugger.StopDebugging);
        script.WaitFor(() => !debugger.Debugging && !running, 10);
        script.Check("pytest: stopping debug clears pending state", () => !running && waiting.Count == 0 && !runningIn.Contains(root));
        script.Run(() => { debugger.breakpoints.Remove(file); Debug(root, [passed], "debug selected pytest case to completion"); });
        script.WaitFor(() => !debugger.Debugging && !running, 30);
        script.Check("pytest: completed debug reports the selected parameterized case", () => Outcome(tests[root].Single(test => test.FullName == passed)) == DotnetTestOutcome.Passed);

        const string completed = "checks/test_cancel.py::test_a_completed", interrupted = "checks/test_cancel.py::test_b_interrupted";
        string marker = Path.Combine(root, "running");
        script.Run(() =>
        {
            File.WriteAllText(Path.Combine(root, "checks", "test_cancel.py"), "import pathlib, time\ndef test_a_completed(): assert True\ndef test_b_interrupted():\n    pathlib.Path('running').write_text('started')\n    time.sleep(60)\n");
            Discover();
        });
        script.WaitFor(() => !discovering && tests.GetValueOrDefault(root)?.Count == 5, 30);
        script.Run(() => Run(root, [completed, interrupted], "cancellable pytest run"));
        script.WaitFor(() => File.Exists(marker), 20);
        script.Check("pytest cancellation: second test is running after the first completed", () => File.Exists(marker) && running);
        script.Run(Cancel);
        script.WaitFor(() => !running, 10);
        script.Check("pytest cancellation: completed result and unrelated prior outcomes are retained", () => results.Count == 4
            && Outcome(tests[root].Single(test => test.FullName == completed)) == DotnetTestOutcome.Passed && failures.Count == 1);
        script.Check("pytest cancellation: interrupted test has no result or pending marker", () => waiting.Count == 0
            && !runningIn.Contains(root) && !ResultsOf(tests[root].Single(test => test.FullName == interrupted)).Any());
        script.Run(() => Run(root, [completed], "rerun after cancellation"));
        script.WaitFor(() => !running, 20);
        script.Check("pytest cancellation: selected rerun works after stopping", () => results.Count == 4
            && Outcome(tests[root].Single(test => test.FullName == completed)) == DotnetTestOutcome.Passed);
    }
}
