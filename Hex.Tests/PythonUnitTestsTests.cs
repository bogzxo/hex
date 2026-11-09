using Hex.Core;

namespace Hex.Tests;

public sealed class PythonUnitTestsTests
{
    [Theory]
    [InlineData("tests", false)]
    [InlineData("test", false)]
    [InlineData("tests", true)]
    [InlineData("checks", true)]
    public async Task NestedPackagesAreDiscoveredWithTheirNativeImportRoot(string container, bool package)
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-nested-tests-" + Guid.NewGuid());
        string parent = Path.Combine(root, container), unit = Path.Combine(parent, "unit");
        Directory.CreateDirectory(unit);
        try
        {
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = 'nested-probe'\nversion = '0.0.0'\n");
            if (package) File.WriteAllText(Path.Combine(parent, "__init__.py"), "");
            File.WriteAllText(Path.Combine(unit, "__init__.py"), "");
            File.WriteAllText(Path.Combine(parent, "helpers.py"), "answer = 42\n");
            string file = Path.Combine(unit, "test_nested.py");
            string import = package ? "from ..helpers import answer" : "from helpers import answer";
            File.WriteAllText(file, "import unittest\n" + import + "\nclass Nested(unittest.TestCase):\n    def test_answer(self): self.assertEqual(answer, 42)\n");
            string folder = PythonUnitTests.DiscoveryFolder(root)!;
            Assert.Equal(package ? root : parent, folder);
            string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTHON");
            if (string.IsNullOrEmpty(python)) return;
            var discovered = await PythonUnitTests.Execute(python, folder, false, projectRoot: root);
            Assert.Empty(discovered["errors"]!.AsArray());
            var test = Assert.Single(discovered["tests"]!.AsArray())!;
            string id = (package ? container + "." : "") + "unit.test_nested.Nested.test_answer";
            Assert.Equal(id, test["id"]!.GetValue<string>());
            Assert.Equal(file, test["file"]!.GetValue<string>());
            var run = await PythonUnitTests.Execute(python, folder, true, [id], projectRoot: root);
            Assert.Equal("passed", Assert.Single(run["results"]!.AsArray())!["outcome"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TestsFolderCanImportProjectModulesAndReadProjectFiles()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-python-project-" + Guid.NewGuid());
        string tests = Path.Combine(root, "tests");
        Directory.CreateDirectory(tests);
        try
        {
            File.WriteAllText(Path.Combine(root, "application.py"), "answer = 42\n");
            File.WriteAllText(Path.Combine(root, "fixture.txt"), "project data");
            File.WriteAllText(Path.Combine(tests, "test_application.py"), """
import pathlib, unittest
from application import answer
class ApplicationTests(unittest.TestCase):
    def test_answer(self):
        self.assertEqual(answer, 42)
        self.assertEqual(pathlib.Path('fixture.txt').read_text(), 'project data')
    def test_other(self): self.fail('must not run when selecting test_answer')
""");
            var discovered = await PythonUnitTests.Execute(python, tests, false, projectRoot: root);
            Assert.Empty(discovered["errors"]!.AsArray());
            Assert.Equal(2, discovered["tests"]!.AsArray().Count);
            var run = await PythonUnitTests.Execute(python, tests, true, ["test_application.ApplicationTests.test_answer"], projectRoot: root);
            var result = Assert.Single(run["results"]!.AsArray());
            Assert.Equal("passed", result!["outcome"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealDiscoveryRunSelectionAndFailures()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        string folder = Path.Combine(Path.GetTempPath(), "hex-unittest-fixture-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "test_sample.py"), """
import unittest
print('import output')
class Sample(unittest.TestCase):
    def test_pass(self): print('test output')
    def test_fail(self): self.assertEqual(1, 2)
    @unittest.skip('not today')
    def test_skip(self): pass
    def test_subtest(self):
        with self.subTest(value=2): self.assertEqual(1, 2)
""");
            var discovered = await PythonUnitTests.Execute(python, folder, false);
            Assert.Equal(4, discovered["tests"]!.AsArray().Count);
            Assert.Empty(discovered["results"]!.AsArray());
            var run = await PythonUnitTests.Execute(python, folder, true);
            var results = run["results"]!.AsArray();
            Assert.Equal(4, results.Count);
            Assert.Equal(2, results.Count(row => row!["outcome"]!.GetValue<string>() == "failed"));
            Assert.Single(results.Where(row => row!["outcome"]!.GetValue<string>() == "skipped"));
            Assert.Contains("test output", run["output"]!.GetValue<string>());
            var selected = await PythonUnitTests.Execute(python, folder, true, ["test_sample.Sample.test_pass"]);
            Assert.Equal("passed", Assert.Single(selected["results"]!.AsArray())!["outcome"]!.GetValue<string>());
            await File.WriteAllTextAsync(Path.Combine(folder, "test_broken.py"), "raise RuntimeError('broken import')");
            var broken = await PythonUnitTests.Execute(python, folder, false);
            Assert.Single(broken["errors"]!.AsArray());
            var failedImport = await PythonUnitTests.Execute(python, folder, true);
            Assert.Contains(failedImport["results"]!.AsArray(), row => row!["message"]!.GetValue<string>().Contains("broken import"));
            File.Delete(Path.Combine(folder, "test_broken.py"));
            await File.WriteAllTextAsync(Path.Combine(folder, "test_wait.py"), """
import pathlib, time, unittest
class Waiting(unittest.TestCase):
    def test_wait(self):
        pathlib.Path('started').write_text('started')
        time.sleep(60)
        pathlib.Path('finished').write_text('finished')
""");
            using var cancel = new CancellationTokenSource();
            Task<System.Text.Json.Nodes.JsonObject> pending = PythonUnitTests.Execute(python, folder, true, ["test_wait.Waiting.test_wait"], cancel.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(folder, "started")) && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.True(File.Exists(Path.Combine(folder, "started")));
            cancel.Cancel();
            var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cancelled["cancelled"]!.GetValue<bool>());
            Assert.Empty(cancelled["results"]!.AsArray());
            Assert.False(File.Exists(Path.Combine(folder, "finished")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task EachTestKeepsItsOwnOutputIncludingSetupTeardownAndErrors()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-unittest-output-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "test_output.py"), """
import sys, unittest
print('module output')
class Output(unittest.TestCase):
    def setUp(self): print('setup ' + self._testMethodName)
    def tearDown(self): print('teardown ' + self._testMethodName)
    def test_first(self):
        print('first stdout')
        print('first stderr', file=sys.stderr)
        self.fail('intentional failure')
    def test_second(self): print('second stdout')
class Quiet(unittest.TestCase):
    def test_quiet(self): pass
""");
            var run = await PythonUnitTests.Execute(python, root, true);
            var rows = run["results"]!.AsArray();
            Assert.Equal(3, rows.Count);
            var first = rows.Single(row => row!["id"]!.GetValue<string>().EndsWith("test_first"))!;
            var second = rows.Single(row => row!["id"]!.GetValue<string>().EndsWith("test_second"))!;
            string firstOutput = first["output"]!.GetValue<string>();
            Assert.Contains("first stdout", firstOutput);
            Assert.Contains("first stderr", firstOutput);
            Assert.Contains("setup test_first", firstOutput);
            Assert.Contains("teardown test_first", firstOutput);
            Assert.DoesNotContain("second stdout", firstOutput);
            Assert.DoesNotContain("module output", firstOutput);
            Assert.Equal("failed", first["outcome"]!.GetValue<string>());
            Assert.Contains("second stdout", second["output"]!.GetValue<string>());
            Assert.DoesNotContain("first stdout", second["output"]!.GetValue<string>());
            Assert.Equal("", rows.Single(row => row!["id"]!.GetValue<string>().EndsWith("test_quiet"))!["output"]!.GetValue<string>());
            Assert.Contains("module output", run["output"]!.GetValue<string>());
            Assert.Contains("first stdout", run["output"]!.GetValue<string>());
            Assert.Contains("second stdout", run["output"]!.GetValue<string>());
            Assert.Contains("first stderr", run["stderr"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }
}
