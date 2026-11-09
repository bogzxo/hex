using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestConfiguredCTest(HexPointerScript script, string build, string configuration, string? name)
    {
        string key = Path.Combine(build, "CTestTestfile.cmake");
        bool Current() => discovered && !discovering && (name is null
            ? !tests.ContainsKey(key) && !ctestProjects.ContainsKey(key)
            : ctestProjects.GetValueOrDefault(key)?.Configuration == configuration
                && tests.GetValueOrDefault(key)?.Count == 1 && tests[key][0].Method == name);
        script.Run(Show);
        script.WaitFor(Current, 30f);
        script.Check("CMake configure: Test Explorer follows " + configuration + " / " + (name ?? "no tests"), Current);
    }

    internal void TestCTestBeforeBuildSelection(HexPointerScript script)
    {
        script.Run(Discover);
        script.WaitFor(() => discovered && !discovering, 30f);
        script.Check("CTest configuration: explorer is discovered before choosing a native build", () => discovered);
    }

    internal void TestSelectedCTest(HexPointerScript script, string root, string source)
    {
        string build = Path.Combine(root, "preset output", "selected");
        string key = Path.Combine(build, "CTestTestfile.cmake");
        script.Run(Show);
        script.WaitFor(() => discovered && !discovering, 30f);
        script.Check("CTest configuration: discovery uses the selected build, not clangd's database", () => tests.GetValueOrDefault(key)?.Count == 1
            && ctestProjects.GetValueOrDefault(key)?.Configuration == "Checked");
        script.Run(() => Run(key, null, "selected CMake configuration"));
        script.WaitFor(() => !running, 30f);
        script.Check("CTest configuration: running builds and passes the selected configuration", () => tests.TryGetValue(key, out var found)
            && found.Count == 1 && Outcome(found[0]) == DotnetTestOutcome.Passed);
        script.Run(() => Debug(key, ["CTest.chosen"], "debug selected configuration"));
        script.WaitFor(() => debugger.DebugStopped, 25f);
        script.Check("CTest configuration: test debugging uses the selected executable", () => debugger.DebugStopped
            && debugger.stoppedFrames.Any(frame => frame.File is not null && PlatformPaths.Same(frame.File, source) && frame.Line == 3));
        script.Run(debugger.StopDebugging);
        script.WaitFor(() => !debugger.Debugging && !running, 10f);
        script.Check("CTest configuration: stopped test leaves no pending run", () => !running && waiting.Count == 0);
        script.Run(() =>
        {
            // A late report from the old configuration must not populate the new one.
            var old = Running(key, ["CTest.chosen"], "old configuration");
            runningIn.Add(key);
            Discover();
            CMakeProject.RememberBuildConfiguration(root, build, "Debug");
            debugger.NativeBuildChanged?.Invoke(root, new(build, "Debug"));
            Finished(key, [new DotnetTestResult("CTest.chosen", DotnetTestOutcome.Passed, TimeSpan.Zero, "", "", "")], "old configuration", 0, old, false);
        });
        script.Check("CTest configuration: obsolete completion clears pending state without publishing results", () => !running && waiting.Count == 0 && !results.Keys.Any(identity => PlatformPaths.Same(identity.Project, key)));
        script.WaitFor(() => discovered && !discovering && ctestProjects.GetValueOrDefault(key)?.Configuration == "Debug", 30f);
        script.Check("CTest configuration: a change during discovery refreshes native test names", () => tests.GetValueOrDefault(key)?.Count == 2
            && tests[key].Any(test => test.Method == "debug_only"));
        script.Run(() =>
        {
            CMakeProject.RememberBuildConfiguration(root, build, "Checked");
            debugger.NativeBuildChanged?.Invoke(root, new(build, "Checked"));
        });
        script.WaitFor(() => discovered && !discovering && ctestProjects.GetValueOrDefault(key)?.Configuration == "Checked", 30f);
        script.Check("CTest configuration: switching back removes configuration-specific tests", () => tests.GetValueOrDefault(key)?.Count == 1);
        script.Run(() => Run(key, null, "rerun after configuration switch"));
        script.WaitFor(() => !running, 30f);
        script.Run(() => debugger.NativeBuildChanged?.Invoke(root, new(build, "Checked")));
        script.Check("CTest configuration: unchanged selection preserves the successful rerun", () => !discovering && discovered
            && tests.TryGetValue(key, out var found) && found.Count == 1 && Outcome(found[0]) == DotnetTestOutcome.Passed);
    }

    internal void TestCTestExplorer(HexPointerScript script, string root, Action<string> open)
    {
        string build = Path.Combine(root, "build", "hex-debug");
        string key = Path.Combine(build, "CTestTestfile.cmake");
        bool prepared = false;
        string? error = null;
        script.Run(() => context.Work.InBackground("Prepare CTest fixture", () =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), """
cmake_minimum_required(VERSION 3.20)
project(CTestEditorProbe LANGUAGES CXX)
enable_testing()
add_executable(probe main.cpp)
add_test(NAME "pass (one)" COMMAND probe 0)
add_test(NAME fail COMMAND probe 1)
add_test(NAME skipped COMMAND probe 2)
set_tests_properties(skipped PROPERTIES SKIP_RETURN_CODE 2)
set_tests_properties("pass (one)" fail skipped PROPERTIES WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}/test cwd" ENVIRONMENT "HEX_CTEST_VALUE=42")
""");
            Directory.CreateDirectory(Path.Combine(root, "test cwd"));
            File.WriteAllText(Path.Combine(root, "test cwd", "fixture.txt"), "fixture");
            File.WriteAllText(Path.Combine(root, "main.cpp"), "#include <stdlib.h>\n#include <stdio.h>\nint main(int argc, char **argv) {\n    int value = argc > 1 ? atoi(argv[1]) : 99;\n    const char *env = getenv(\"HEX_CTEST_VALUE\");\n    FILE *file = fopen(\"fixture.txt\", \"r\");\n    if (!env || atoi(env) != 42 || !file) return 99;\n    fclose(file);\n    value += 0;\n    return value;\n}\n");
            return CMakeProject.Configure(CMakeProject.Tool("cmake")!, root, build, default).GetAwaiter().GetResult();
        }, result => { error = result.ExitCode == 0 ? null : result.Output; prepared = true; }, quiet: true));
        script.WaitFor(() => prepared, 30f);
        script.Check("CTest: project configured for native discovery", () => prepared && error is null);
        script.Run(() => { open(root); Show(); });
        script.WaitFor(() => discovered && !discovering, 30f);
        script.Check("CTest: native test names appear in Test Explorer", () => tests.GetValueOrDefault(key)?.Count == 3 && tests[key].Any(test => test.Method == "pass (one)"));
        script.Run(() => Run(key, null, "CTest suite"));
        script.WaitFor(() => !running, 30f);
        script.Check("CTest: build then run reports pass, fail and skip", () => tests.TryGetValue(key, out var found) && found.Select(Outcome).Order().SequenceEqual(new[] { DotnetTestOutcome.Passed, DotnetTestOutcome.Failed, DotnetTestOutcome.Skipped }.Order()));
        script.Run(() => Run(key, ["CTest.pass (one)"], "Selected CTest"));
        script.WaitFor(() => !running, 20f);
        script.Check("CTest: exact selected rerun keeps other outcomes", () => tests.TryGetValue(key, out var found)
            && ResultsOf(found.Single(test => test.Method == "pass (one)")).SingleOrDefault() is { Outcome: DotnetTestOutcome.Passed, IsCase: false }
            && Outcome(found.Single(test => test.Method == "fail")) == DotnetTestOutcome.Failed);
        script.Run(() => { if (tests.TryGetValue(key, out var found)) GoTo(found[0]); });
        script.Check("CTest: source navigation opens the CMake test declaration", () => context.Documents.Current.Path is { } file && PlatformPaths.Same(file, Path.Combine(root, "CMakeLists.txt")));
        string source = Path.Combine(root, "main.cpp");
        script.Run(() =>
        {
            context.Documents.OpenAt(source, 8, 0);
            debugger.ToggleBreakpoint(source, 8);
            Debug(key, ["CTest.pass (one)"], "Debug selected CTest");
        });
        script.WaitFor(() => debugger.DebugStopped, 25f);
        script.Check("CTest: selected debug stops after environment and working-directory checks", () => debugger.DebugStopped && debugger.stoppedFrames.Any(frame => frame.File is not null && PlatformPaths.Same(frame.File, source) && frame.Line == 8));
        script.Run(debugger.StepOver);
        script.WaitFor(() => debugger.DebugStopped && context.Documents.Current.Editor.ExecutionLine == 9, 10f);
        script.Check("CTest: native source stepping works", () => debugger.DebugStopped && context.Documents.Current.Editor.ExecutionLine == 9);
        script.Run(debugger.StopDebugging);
        script.WaitFor(() => !debugger.Debugging, 10f);
        script.Check("CTest: Stop terminates the selected test", () => !debugger.Debugging);
        script.Check("CTest: stopped debug clears its old result and pending state only", () => !running
            && !runningIn.Contains(key) && waiting.Count == 0
            && !ResultsOf(tests[key].Single(test => test.Method == "pass (one)")).Any()
            && Outcome(tests[key].Single(test => test.Method == "fail")) == DotnetTestOutcome.Failed);
        script.Run(() => debugger.breakpoints.Clear());
        foreach (var (name, expected) in new[] { ("pass (one)", DotnetTestOutcome.Passed), ("fail", DotnetTestOutcome.Failed), ("skipped", DotnetTestOutcome.Skipped) })
        {
            script.Run(() => Debug(key, ["CTest." + name], "Debug " + name));
            script.WaitFor(() => !debugger.Debugging && !running, 25f);
            script.Check("CTest: debug result reports " + expected, () => ResultsOf(tests[key].Single(test => test.Method == name)).SingleOrDefault()
                is { IsCase: false } result && result.Outcome == expected && result.Output.Contains("Debugged process exited"));
        }
        script.Run(() =>
        {
            debugger.ToggleBreakpoint(source, 8);
            Debug(key, ["CTest.pass (one)"], "Debug restart CTest");
        });
        script.WaitFor(() => debugger.DebugStopped, 25f);
        script.Run(debugger.RestartDebugging);
        script.WaitFor(() => debugger.DebugStopped, 25f);
        script.Check("CTest: restarting debug owns exactly one pending run", () => debugger.DebugStopped && runs == 1 && waiting.Count == 1);
        script.Run(Cancel);
        script.WaitFor(() => !debugger.Debugging, 10f);
        script.Check("CTest: Test Explorer Cancel stops debugging and clears pending state", () => !debugger.Debugging && !running && waiting.Count == 0 && !runningIn.Contains(key));
        script.Run(() => debugger.breakpoints.Clear());
    }
}
