using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestCTestBuildProblems(HexPointerScript test, string root)
    {
        string file = Path.Combine(root, "main.cpp");
        string project = Path.Combine(root, "build", "hex-debug", "CTestTestfile.cmake");
        string original = "";
        test.Run(() =>
        {
            Open(file);
            original = code.Text;
            code.Text = original.Replace("value += 0;", "value += missing_test_value;", StringComparison.Ordinal);
            Save();
            testExplorer.Run(project, ["CTest.pass (one)"], "Broken C++ test build");
        });
        test.WaitFor(() => Context.Work.Find("ctest:" + project) is null, 30f);
        test.Settle(0.2f);
        test.Check("CTest build: compiler error reaches Problems at the source location", () => terminalProblems.Any(problem =>
            PlatformPaths.Same(problem.Path, file) && problem.Line == 8 && !problem.Warning && problem.Text.Contains("missing_test_value")));
        test.Run(() => Descendants(problems).OfType<Button>().FirstOrDefault(button => button.Tooltip.Contains("missing_test_value"))?.OnPressed?.Invoke());
        test.Check("CTest build: Problems row navigates to the compiler error", () => PlatformPaths.Same(document.Path!, file)
            && code.Document.Caret.Line == 8 && code.Document.Caret.Column > 0);
        test.Run(() =>
        {
            code.Text = original;
            Save();
            // An old build diagnostic on another file proves that rebuilding clears
            // the build's Problems rather than only the editor's save clearing them.
            Context.Terminals.AddProblem(Path.Combine(root, "CMakeLists.txt"), 1, 1, "stale test build error", false);
            testExplorer.Run(project, ["CTest.pass (one)"], "Fixed C++ test build");
        });
        test.WaitFor(() => Context.Work.Find("ctest:" + project) is null, 30f);
        test.Settle(0.2f);
        test.Check("CTest build: successful rebuild clears old compiler diagnostics", () => !terminalProblems.Any(problem =>
            problem.Text.Contains("missing_test_value") || problem.Text == "stale test build error"));
    }
}
