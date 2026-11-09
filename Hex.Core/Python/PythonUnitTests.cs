using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal static class PythonUnitTests
{
    internal static string? DiscoveryFolder(string root)
    {
        if (PythonPytest.IsProject(root)) return root;
        if (Directory.EnumerateFiles(root, "test*.py").Any() || Directory.EnumerateFiles(root, "*_test.py").Any()) return root;
        foreach (string name in new[] { "tests", "test" })
        {
            string folder = Path.Combine(root, name);
            if (Directory.Exists(folder)) return File.Exists(Path.Combine(folder, "__init__.py")) ? root : folder;
        }
        // Let unittest decide which packages contain tests. Looking only for direct
        // test*.py files hides package-only suites and custom test layouts.
        if (new[] { "pyproject.toml", "setup.py", "setup.cfg" }.Any(name => File.Exists(Path.Combine(root, name)))
            || Directory.EnumerateDirectories(root).Any(folder => File.Exists(Path.Combine(folder, "__init__.py"))))
            return root;
        // Unconfigured pytest suites can live anywhere under the project. Do not
        // import code here or mistake installed packages for project tests.
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? folder))
        {
            foreach (string child in Directory.EnumerateDirectories(folder))
            {
                string name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "venv" or "env" or "node_modules" or "site-packages" or "__pycache__" or "build" or "dist"
                    || File.Exists(Path.Combine(child, "pyvenv.cfg")) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.EnumerateFiles(child, "test*.py").Any() || Directory.EnumerateFiles(child, "*_test.py").Any()) return root;
                pending.Push(child);
            }
        }
        return null;
    }

    // Results use a separate file so output from imported modules and tests cannot corrupt the protocol.
    internal const string Runner = """
import inspect, io, json, os, sys, time, traceback, unittest
destination, mode, root, *selected = sys.argv[1:]
sys.path.insert(0, root)
loader = unittest.TestLoader()
suite = loader.discover(root)
def flatten(suite):
    for test in suite:
        if isinstance(test, unittest.TestSuite): yield from flatten(test)
        else: yield test
tests = list(flatten(suite))
def describe(test):
    row = {'id': test.id(), 'file': '', 'line': 0}
    try:
        method = getattr(type(test), test._testMethodName)
        row.update(file=inspect.getsourcefile(method) or '', line=inspect.getsourcelines(method)[1] - 1)
    except (AttributeError, TypeError, OSError): pass
    return row
rows = [describe(test) for test in tests]
results = []
def save():
    temporary = destination + '.tmp'
    with open(temporary, 'w', encoding='utf-8') as stream:
        json.dump({'tests': rows, 'results': results, 'errors': loader.errors}, stream)
    os.replace(temporary, destination)
class TestOutput:
    def __init__(self, stream, captured):
        self.stream, self.captured = stream, captured
    def write(self, text):
        self.captured.write(text)
        return self.stream.write(text)
    def flush(self): self.stream.flush()
    def __getattr__(self, name): return getattr(self.stream, name)
class Result(unittest.TestResult):
    def startTest(self, test):
        super().startTest(test)
        self.started = time.monotonic()
        self.row = dict(describe(test), outcome='passed', message='', stack='', duration=0)
        self.captured = io.StringIO()
        self.stdout, self.stderr = sys.stdout, sys.stderr
        sys.stdout, sys.stderr = TestOutput(self.stdout, self.captured), TestOutput(self.stderr, self.captured)
    def stopTest(self, test):
        sys.stdout, sys.stderr = self.stdout, self.stderr
        self.row['output'] = self.captured.getvalue()
        self.row['duration'] = time.monotonic() - self.started
        results.append(self.row)
        if mode in ('run', 'debug'): save()
        super().stopTest(test)
    def failure(self, test, err):
        stack = ''.join(traceback.format_exception(*err))
        row = self.row if hasattr(self, 'row') and self.row['id'] == test.id() else dict(describe(test), duration=0)
        row.update(outcome='failed', message=str(err[1]), stack=stack)
        frames = traceback.extract_tb(err[2])
        if frames: row.update(failureFile=frames[-1].filename, failureLine=frames[-1].lineno-1)
        if row is not getattr(self, 'row', None):
            results.append(row)
            if mode in ('run', 'debug'): save()
    def addError(self, test, err):
        super().addError(test, err)
        self.failure(test, err)
    def addFailure(self, test, err):
        super().addFailure(test, err)
        self.failure(test, err)
    def addSkip(self, test, reason):
        super().addSkip(test, reason)
        if hasattr(self, 'row') and self.row['id'] == test.id(): self.row.update(outcome='skipped', message=reason)
        else:
            results.append(dict(describe(test), outcome='skipped', message=reason, stack='', duration=0))
            if mode in ('run', 'debug'): save()
    def addExpectedFailure(self, test, err):
        super().addExpectedFailure(test, err)
        self.row.update(outcome='skipped', message='Expected failure: ' + str(err[1]))
    def addUnexpectedSuccess(self, test):
        super().addUnexpectedSuccess(test)
        self.row.update(outcome='failed', message='Unexpected success')
    def addSubTest(self, test, subtest, err):
        super().addSubTest(test, subtest, err)
        if err is not None: self.failure(test, err)
save()
if mode in ('run', 'debug'):
    chosen = [test for test in tests if not selected or any(test.id() == name or test.id().startswith(name.rstrip('.') + '.') for name in selected)]
    unittest.TestSuite(chosen).run(Result())
save()
""";

    internal static Dictionary<string, string> ImportEnvironment(string folder, string projectRoot)
    {
        string inherited = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
        string paths = Path.GetFullPath(folder) + Path.PathSeparator + Path.GetFullPath(projectRoot);
        return new() { ["PYTHONPATH"] = paths + (inherited.Length == 0 ? "" : Path.PathSeparator + inherited) };
    }

    internal static string RunnerFor(string projectRoot) => PythonPytest.IsProject(projectRoot) ? PythonPytest.Runner
        : PythonPytest.Detect + "\nexec(" + JsonValue.Create(PythonPytest.Runner)!.ToJsonString()
            + " if use_pytest else " + JsonValue.Create(Runner)!.ToJsonString() + ")\n";

    public static async Task<JsonObject> Execute(string python, string folder, bool run, IReadOnlyList<string>? selected = null, CancellationToken cancel = default, string? projectRoot = null)
    {
        string result = Path.Combine(Path.GetTempPath(), "hex-unittest-" + Guid.NewGuid() + ".json");
        try
        {
            projectRoot ??= folder;
            var start = new ProcessStartInfo(python) { WorkingDirectory = projectRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var (name, value) in ImportEnvironment(folder, projectRoot)) start.Environment[name] = value;
            foreach (string argument in new[] { "-c", RunnerFor(projectRoot), result, run ? "run" : "discover", Path.GetFullPath(folder) }) start.ArgumentList.Add(argument);
            foreach (string name in selected ?? []) start.ArgumentList.Add(name);
            cancel.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new IOException("Python test discovery could not start.");
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            bool stopped = false;
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                if (!cancel.IsCancellationRequested) throw new TimeoutException("Python tests exceeded ten minutes.");
                stopped = true;
            }
            string output = await stdout, errors = await stderr;
            if (errors.Contains("No module named 'pytest'", StringComparison.Ordinal))
                throw new IOException($"pytest is required for this project. Install it in the selected Python environment with: \"{python}\" -m pip install pytest");
            if (!stopped && (process.ExitCode != 0 || !File.Exists(result))) throw new IOException($"Python tests exited with {process.ExitCode}: {errors}");
            // The runner atomically checkpoints completed tests. After killing the process, read
            // the last checkpoint without the cancelled token; an interrupted test has no result.
            var report = File.Exists(result)
                ? JsonNode.Parse(await File.ReadAllTextAsync(result, CancellationToken.None))!.AsObject()
                : new JsonObject { ["tests"] = new JsonArray(), ["results"] = new JsonArray(), ["errors"] = new JsonArray() };
            if (stopped) report["cancelled"] = true;
            report["output"] = output;
            report["stderr"] = errors;
            return report;
        }
        finally
        {
            if (File.Exists(result)) File.Delete(result);
            if (File.Exists(result + ".tmp")) File.Delete(result + ".tmp");
        }
    }
}
