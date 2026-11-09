using Horizon.UI.Components;

namespace Hex.Tests;

public sealed class PythonDiagnosticTests
{
    [Theory]
    [InlineData("Exception: failure")]
    [InlineData("KeyboardInterrupt")]
    [InlineData("package.CustomFailure: invalid input")]
    [InlineData("ValueError: invalid value")]
    public void ReportsExceptionAtDeepestFrame(string exception)
    {
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed("Traceback (most recent call last):\n  File \"main.py\", line 2, in <module>\n    run()\n  File \"worker.py\", line 7, in run\n    raise failure\n" + exception + "\n");
        var problem = Assert.Single(problems);
        Assert.Equal("worker.py", problem.File);
        Assert.Equal(7, problem.Line);
        Assert.Equal(exception, problem.Message);
    }

    [Fact]
    public void UnlocatedExceptionIsNotAssignedToNextTraceback()
    {
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed("ValueError: previous run\nTraceback (most recent call last):\n  File \"current.py\", line 4\n    broken()\nTypeError: current run\n");
        Assert.Equal("TypeError: current run", Assert.Single(problems).Message);
    }
}
