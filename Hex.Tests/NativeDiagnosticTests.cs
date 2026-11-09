using Horizon.UI.Components;

namespace Hex.Tests;

public sealed class NativeDiagnosticTests
{
    [Theory]
    [InlineData("PS C:\\project> main.cpp(2): error C2065: echoed command")]
    [InlineData("main.cpp(2,3): note: declared here")]
    [InlineData("PS> main.cpp:2: error: echoed command")]
    [InlineData("main.cpp:2: note: declared here")]
    public void PromptsAndInformationalNotesAreNotErrors(string output)
    {
        var problems = new List<TerminalProblem>();
        new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add).Feed(output + "\n");
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("C:/project/broken file.cpp(1,21): error: use of undeclared identifier 'missing_value'", "C:/project/broken file.cpp", 1, 21, false, "use of undeclared identifier 'missing_value'")]
    [InlineData("main.cpp(8): error C2065: 'missing': undeclared identifier", "main.cpp", 8, 0, false, "C2065: 'missing': undeclared identifier")]
    [InlineData("1>C:\\project\\main.cpp(9,4): warning C4101: 'value': unreferenced local variable", "C:\\project\\main.cpp", 9, 4, true, "C4101: 'value': unreferenced local variable")]
    [InlineData("main.cpp(1): fatal error C1083: Cannot open include file", "main.cpp", 1, 0, false, "C1083: Cannot open include file")]
    [InlineData("main.cpp:2:3: error: unknown name", "main.cpp", 2, 3, false, "unknown name")]
    [InlineData("main.cpp:2: error: unknown name", "main.cpp", 2, 0, false, "unknown name")]
    [InlineData("C:/project with spaces/main.cpp:14: warning: unused variable", "C:/project with spaces/main.cpp", 14, 0, true, "unused variable")]
    [InlineData("C:\\project\\main.cxx:7: fatal error: missing header", "C:\\project\\main.cxx", 7, 0, false, "missing header")]
    public void NativeErrorsReachProblemsWithNavigableLocations(string output, string file, int line, int column, bool warning, string message)
    {
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed(output + "\r\n");
        var problem = Assert.Single(problems);
        Assert.Equal(file, problem.File);
        Assert.Equal(line, problem.Line);
        Assert.Equal(column, problem.Column);
        Assert.Equal(warning, problem.Warning);
        Assert.Equal(message, problem.Message);
    }

    [Fact]
    public async Task Clang_without_columns_still_produces_navigable_problems()
    {
        string? compiler = Environment.GetEnvironmentVariable("HEX_TEST_CLANG");
        if (string.IsNullOrEmpty(compiler)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex clang lines " + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "broken.cpp");
            File.WriteAllText(source, "int answer() {\n    return missing_value;\n}\n");
            var result = await Hex.Core.CMakeProject.Run(compiler, root,
                ["-fsyntax-only", "-fno-show-column", "-fno-caret-diagnostics", "-fdiagnostics-format=clang", source], CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            List<TerminalProblem> problems = [];
            new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add).Feed(result.Output + "\n");
            var problem = Assert.Single(problems);
            Assert.Equal(source.Replace('\\', '/'), problem.File.Replace('\\', '/'));
            Assert.Equal(2, problem.Line);
            Assert.Equal(0, problem.Column);
            Assert.Contains("missing_value", problem.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
