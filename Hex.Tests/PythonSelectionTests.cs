using Hex.Core;
using Hex.Dawn;
using Horizon.UI.Scripting;

namespace Hex.Tests;

public class PythonSelectionTests
{
    [Fact]
    public void LongTerminalIdentityHasABoundedDistinctCacheFilename()
    {
        var profile = new Horizon.UI.Components.TerminalProfile("Python", "powershell", id: "hex-python:" + new string('a', 300));
        string file = Horizon.UI.Components.TerminalIntegration.CommandsFile(profile);
        Assert.True(Path.GetFileName(file).Length < 100);
        Assert.Equal(file, Horizon.UI.Components.TerminalIntegration.CommandsFile(profile));
        profile.Id += "b";
        Assert.NotEqual(file, Horizon.UI.Components.TerminalIntegration.CommandsFile(profile));
    }

    [Fact]
    public void TerminalEnvironmentPreservesIntegrationAndDoesNotMutateInput()
    {
        string interpreter = Path.Combine(Path.GetTempPath(), "selected python", "python.exe");
        var inherited = new Dictionary<string, string> { ["PATH"] = "integration-path", ["DUSK_HOST"] = "windows" };
        var result = PythonEnvironment.TerminalEnvironment(interpreter, inherited);
        Assert.Equal(Path.GetDirectoryName(interpreter) + Path.PathSeparator + "integration-path", result["PATH"]);
        Assert.Equal("windows", result["DUSK_HOST"]);
        Assert.Equal("", result["PYTHONHOME"]);
        Assert.Equal("integration-path", inherited["PATH"]);
        Assert.False(inherited.ContainsKey("VIRTUAL_ENV"));
    }

    [Fact]
    public void SelectedInterpreterOverridesLocalDetectionAndCanBeCleared()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-choice-" + Guid.NewGuid());
        string local = PythonEnvironment.ExecutableIn(Path.Combine(root, ".venv"));
        string selected = PythonEnvironment.ExecutableIn(Path.Combine(root, "custom environment"));
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        Directory.CreateDirectory(Path.GetDirectoryName(selected)!);
        try
        {
            File.WriteAllText(local, "fixture");
            File.WriteAllText(selected, "fixture");
            Assert.Equal(local, PythonEnvironment.FindLocal(root, root));
            PythonEnvironment.Select(root, selected);
            Assert.Equal(selected, PythonEnvironment.Selected(root));
            Assert.Equal(selected, PythonEnvironment.FindLocal(Path.Combine(root, "nested"), root));
            Assert.Contains("custom environment", HexTasks.PythonRun(root, root, "main.py", "powershell"));
            var entry = new LanguageServers.Registration("python", "pyright-langserver", [], "python", [".py"]);
            Assert.Equal(selected, HexLanguages.PythonServerSettings(entry, Path.Combine(root, "main.py"), root)!["python"]!["pythonPath"]!.GetValue<string>());
            Assert.Throws<FileNotFoundException>(() => PythonEnvironment.Select(root, Path.Combine(root, "missing.exe")));
            Assert.Equal(selected, PythonEnvironment.Selected(root));
            PythonEnvironment.Select(root, null);
            Assert.Equal(local, PythonEnvironment.FindLocal(root, root));
        }
        finally { PythonEnvironment.Select(root, null); Directory.Delete(root, true); }
    }
}
