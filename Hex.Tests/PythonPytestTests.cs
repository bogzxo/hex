using Hex.Core;

namespace Hex.Tests;

public class PythonPytestTests
{
    [Theory]
    [InlineData("node_modules", false)]
    [InlineData(".cache", false)]
    [InlineData("custom_environment", true)]
    [InlineData("build", false)]
    public void VendorTestsDoNotCreateAProjectTestSuite(string directory, bool virtualEnvironment)
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-exclusions-" + Guid.NewGuid());
        string dependency = Path.Combine(root, directory);
        Directory.CreateDirectory(Path.Combine(dependency, "nested"));
        try
        {
            if (virtualEnvironment) File.WriteAllText(Path.Combine(dependency, "pyvenv.cfg"), "home = unused");
            File.WriteAllText(Path.Combine(dependency, "nested", "test_vendor.py"), "def test_vendor(): pass");
            Assert.Null(PythonUnitTests.DiscoveryFolder(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("test_plain.py", "def test_answer(): assert 6 * 7 == 42\n", true)]
    [InlineData("answer_test.py", "class TestAnswer:\n    def test_answer(self): assert 6 * 7 == 42\n", true)]
    [InlineData("tests/test_plain.py", "def test_answer(): assert 6 * 7 == 42\n", true)]
    [InlineData("checks/unit/test_plain.py", "def test_answer(): assert 6 * 7 == 42\n", true)]
    [InlineData("checks/unit/answer_test.py", "def test_answer(): assert 6 * 7 == 42\n", true)]
    [InlineData("tests/test_plain.py", "import unittest\nclass Answer(unittest.TestCase):\n    def test_answer(self): self.assertEqual(6 * 7, 42)\n", false)]
    public async Task UnconfiguredProjectsSelectTheFrameworkFromRealTestSyntax(string path, string source, bool pytest)
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTEST");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-pytest-auto-" + Guid.NewGuid());
        string file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            File.WriteAllText(file, source);
            // Text in comments/strings and tests inside environments must not switch unittest to pytest.
            File.AppendAllText(file, "\n# def test_not_real(): pass\nexample = '''\ndef test_not_real(): pass\n'''\n");
            string environment = Path.Combine(root, "custom_environment");
            Directory.CreateDirectory(environment);
            File.WriteAllText(Path.Combine(environment, "pyvenv.cfg"), "home = unused\n");
            File.WriteAllText(Path.Combine(environment, "test_vendor.py"), "def test_vendor(): assert False\n");
            string folder = PythonUnitTests.DiscoveryFolder(root)!;
            Assert.NotNull(folder);
            var discovered = await PythonUnitTests.Execute(python, folder, false, projectRoot: root);
            Assert.Equal(pytest, discovered["framework"]?.GetValue<string>() == "pytest");
            var test = Assert.Single(discovered["tests"]!.AsArray())!;
            var run = await PythonUnitTests.Execute(python, folder, true, [test["id"]!.GetValue<string>()], projectRoot: root);
            Assert.Equal("passed", Assert.Single(run["results"]!.AsArray())!["outcome"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("pytest.ini", "")]
    [InlineData(".pytest.ini", "")]
    [InlineData("pytest.toml", "[pytest]\n")]
    [InlineData("pyproject.toml", "[tool.pytest.ini_options]\ntestpaths = ['checks']\n")]
    [InlineData("pyproject.toml", "[tool.pytest]\ntestpaths = ['checks']\n")]
    [InlineData("tox.ini", "[pytest]\n")]
    [InlineData("setup.cfg", "[tool:pytest]\n")]
    public void ProjectConfigurationSelectsNativePytest(string name, string content)
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-pytest-config-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(root, name), content);
            Assert.True(PythonPytest.IsProject(root));
            Assert.Equal(root, PythonUnitTests.DiscoveryFolder(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NativeDiscoveryPreservesParameterizedIdsAndSelectedExecution()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTEST");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-pytest-native-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "checks"));
        try
        {
            File.WriteAllText(Path.Combine(root, "pytest.ini"), "[pytest]\ntestpaths = checks\n");
            File.WriteAllText(Path.Combine(root, "test_ignored.py"), "def test_outside_config(): assert False\n");
            string file = Path.Combine(root, "checks", "test_sample.py");
            File.WriteAllText(file, """
import pytest
@pytest.fixture
def answer(): return 42
@pytest.mark.parametrize('number', [0, 1], ids=['zero.one(0)', 'one.case(1)'])
def test_number(number, answer):
    print('fixture output')
    assert number == 0 and answer == 42
@pytest.mark.skip(reason='not today')
def test_skip(): pass
@pytest.fixture
def broken(): raise RuntimeError('fixture failed')
def test_setup(broken): pass
""");
            var discovered = await PythonUnitTests.Execute(python, root, false);
            Assert.Equal("pytest", discovered["framework"]!.GetValue<string>());
            Assert.Empty(discovered["errors"]!.AsArray());
            Assert.Equal(4, discovered["tests"]!.AsArray().Count);
            string id = "checks/test_sample.py::test_number[zero.one(0)]";
            Assert.Contains(discovered["tests"]!.AsArray(), row => row!["id"]!.GetValue<string>() == id && row["file"]!.GetValue<string>() == file);
            var selected = await PythonUnitTests.Execute(python, root, true, [id]);
            var single = Assert.Single(selected["results"]!.AsArray())!;
            Assert.Equal(id, single["id"]!.GetValue<string>());
            Assert.Equal("passed", single["outcome"]!.GetValue<string>());
            Assert.Contains("fixture output", single["output"]!.GetValue<string>());
            var all = await PythonUnitTests.Execute(python, root, true);
            Assert.Equal(4, all["results"]!.AsArray().Count);
            Assert.Equal(2, all["results"]!.AsArray().Count(row => row!["outcome"]!.GetValue<string>() == "failed"));
            var failed = all["results"]!.AsArray().Single(row => row!["id"]!.GetValue<string>().EndsWith("one.case(1)]"))!;
            Assert.Equal(file, failed["failureFile"]!.GetValue<string>());
            Assert.Equal(6, failed["failureLine"]!.GetValue<int>());
            Assert.Contains("AssertionError", failed["stack"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingPytestExplainsHowToInstallIntoTheSelectedInterpreter()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTHON_WITHOUT_PYTEST");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-pytest-missing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "pytest.ini"), "[pytest]\n");
            var error = await Assert.ThrowsAsync<IOException>(() => PythonUnitTests.Execute(python, root, false));
            Assert.Contains(python, error.Message);
            Assert.Contains("-m pip install pytest", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CollectionFailureIsReportedInsteadOfAnEmptySuccessfulSuite()
    {
        string? python = Environment.GetEnvironmentVariable("HEX_TEST_PYTEST");
        if (string.IsNullOrEmpty(python)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-pytest-errors-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "pytest.ini"), "[pytest]\n");
            File.WriteAllText(Path.Combine(root, "test_broken.py"), "raise RuntimeError('collection failed')\n");
            var report = await PythonUnitTests.Execute(python, root, false);
            Assert.Contains("collection failed", Assert.Single(report["errors"]!.AsArray())!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }
}
