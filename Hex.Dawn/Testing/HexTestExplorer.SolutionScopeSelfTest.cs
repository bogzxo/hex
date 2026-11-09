using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestSolutionScope(HexPointerScript script, string solution)
    {
        script.Run(Discover);
        script.WaitFor(() => discovered && !discovering, 30f);
        script.Check("repository: Test Explorer contains exactly the solution's test projects", () =>
        {
            var expected = DotnetTests.TestProjects(context.Workspace.Roots, solution).ToHashSet(PlatformPaths.Comparer);
            Console.WriteLine($"[Hex test scope] {tests.Count} projects: {string.Join(", ", tests.Keys)}");
            return expected.Count > 0 && expected.SetEquals(tests.Keys);
        });
    }
}
