using Hex.Dawn;

namespace Hex.Tests;

public sealed class PythonRunTests
{
    [Fact]
    public void UsesNearestEnvironmentAndDoesNotCrossWorkspaceBoundary()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-python-" + Guid.NewGuid());
        string source = Path.Combine(root, "src");
        string executable = Path.Combine(root, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "");
        try
        {
            Assert.Contains(".venv", HexTasks.PythonRun(source, root, "hello world.py", "pwsh"));
            Assert.StartsWith("& '", HexTasks.PythonRun(source, root, "hello world.py", "pwsh"));
            Assert.DoesNotContain(".venv", HexTasks.PythonRun(source, source, "main.py", "pwsh"));
            Assert.Equal("python3 'main.py'", HexTasks.PythonRun(source, root, "main.py", "wsl-bash"));
        }
        finally { Directory.Delete(root, true); }
    }
}
