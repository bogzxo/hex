using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestPythonEnvironments(HexPointerScript script, string root, Action<string> open, Action<string, string?> select)
    {
        string folder = Path.Combine(root, "tests"), file = Path.Combine(folder, "test_environment.py");
        string first = PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv"));
        string second = PythonEnvironment.ExecutableIn(Path.Combine(root, "custom environment"));
        Task? setup = null;
        RunScope? obsolete = null;
        script.Run(() => setup = Task.Run(async () =>
        {
            Directory.CreateDirectory(folder);
            foreach (var (environment, value) in new[] { (".venv", "first"), ("custom environment", "second") })
            {
                string directory = Path.Combine(root, environment);
                var created = await CMakeProject.Run("python", root, ["-m", "venv", "--without-pip", directory], default);
                if (created.ExitCode != 0) throw new IOException(created.Output);
                var installed = await CMakeProject.Run(PythonEnvironment.ExecutableIn(directory), root,
                    ["-c", "import pathlib,sysconfig; pathlib.Path(sysconfig.get_path('purelib'), 'environment_choice.py').write_text('name = \"" + value + "\"\\n')"], default);
                if (installed.ExitCode != 0) throw new IOException(installed.Output);
            }
            File.WriteAllText(file, "import unittest\nfrom environment_choice import name\nclass Environment(unittest.TestCase):\n    if name == 'first':\n        def test_first(self): self.assertEqual(name, 'first')\n    else:\n        def test_second(self): self.assertEqual(name, 'second')\n");
        }));
        script.WaitFor(() => setup?.IsCompleted == true, 30f);
        script.Check("Python test environment: native fixtures created", () => setup?.IsCompletedSuccessfully == true);
        script.Run(() => { open(root); context.Documents.OpenAt(file, 0, 0); Show(); });
        bool Found(string method) => discovered && !discovering && tests.GetValueOrDefault(folder) is { Count: 1 } found && found[0].Method == method;
        script.WaitFor(() => Found("test_first"), 30f);
        script.Check("Python test environment: initial interpreter discovers its test", () => Found("test_first"));
        script.Run(() => Run(folder, null, "first environment"));
        script.WaitFor(() => !running, 30f);
        script.Check("Python test environment: initial test passes", () => results.Count == 1 && Outcome(tests[folder][0]) == DotnetTestOutcome.Passed);
        script.Run(() =>
        {
            obsolete = Running(folder, null, "old environment completion");
            select(root, second);
        });
        script.Check("Python test environment: old results cleared immediately", () => results.Count == 0);
        script.WaitFor(() => Found("test_second"), 30f);
        script.Check("Python test environment: selection automatically rediscovers tests", () => Found("test_second"));
        script.Run(() =>
        {
            if (obsolete is not null) Finished(folder,
                [new DotnetTestResult("test_environment.Environment.test_first", DotnetTestOutcome.Passed, TimeSpan.Zero, "", "", "")],
                "old environment completion", 0, obsolete, false);
        });
        script.Check("Python test environment: late completion cannot restore old tests", () => Found("test_second") && results.Count == 0 && !running && waiting.Count == 0);
        script.Run(() => Run(folder, null, "second environment"));
        script.WaitFor(() => !running, 30f);
        script.Check("Python test environment: selected interpreter runs newly discovered test", () => results.Count == 1 && Outcome(tests[folder][0]) == DotnetTestOutcome.Passed);
        script.Run(() => select(root, null));
        script.WaitFor(() => Found("test_first"), 30f);
        script.Check("Python test environment: automatic detection restores original test list", () => Found("test_first") && results.Count == 0);
    }
}
