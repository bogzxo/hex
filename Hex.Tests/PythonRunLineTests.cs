using Hex.Core;

namespace Hex.Tests;

public sealed class PythonRunLineTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-python-run-" + Guid.NewGuid());
    public PythonRunLineTests() { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "main.py"), ""); }
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void PreservesLiteralArgumentsAndExplicitInterpreter()
    {
        var run = PythonRunLine.Of("& '.venv/Scripts/python.exe' 'main.py' 'hello world' '' 'it''s fine'", folder);
        Assert.NotNull(run);
        Assert.Equal(Path.Combine(folder, "main.py"), run.File);
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, ".venv/Scripts/python.exe")), run.Interpreter);
        Assert.Equal(new[] { "hello world", "", "it's fine" }, run.Arguments);
        Assert.Equal(folder, run.Folder);
    }

    [Theory]
    [InlineData("python main.py && echo done")]
    [InlineData("python main.py\necho done")]
    [InlineData("python main.py > output.txt")]
    [InlineData("python main.py $value")]
    [InlineData("python -m")]
    [InlineData("python -m package..module")]
    [InlineData("python 'main.py")]
    [InlineData("python main.py \"$value\"")]
    public void LeavesShellExpressionsAndNonScriptCommandsToTerminal(string command) => Assert.Null(PythonRunLine.Of(command, folder));

    [Fact]
    public void PreservesModuleAndTestRunnerArguments()
    {
        var run = PythonRunLine.Of("python -m pytest -k 'test one' --maxfail=1", folder);
        Assert.NotNull(run);
        Assert.Equal("pytest", run.Module);
        Assert.Equal(new[] { "-k", "test one", "--maxfail=1" }, run.Arguments);
        Assert.Equal(folder, run.Folder);
    }
}
