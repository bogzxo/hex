using Hex.Core.Projects;

namespace Hex.Dawn;

// The tests that failed in the Problems panel as well, the way Visual Studio's Error List has them, each at the line of
// its stack's first frame that's a file of the workspace (the assert that failed, or the helper it called), and gone
// again once a run of it passes. They're what the results say, worked out again when a run's results come in
internal sealed partial class HexTestExplorer
{
    private List<HexProblem> failures = [];

    // Whether anything failed last time it ran, with a place to go to or not, what Run Failed Tests is there for
    private bool anyFailed;

    // How much of a failure's message goes in its row, xUnit's say what was expected and what came on the lines after
    private const int LONGEST_PROBLEM = 240;

    /// <summary>The failed tests the Problems panel lists, at where they failed.</summary>
    private IReadOnlyList<HexProblem> Failures() => failures;

    // The failures put together again from the results, and the panel told
    private void FailuresChanged()
    {
        var now = new List<HexProblem>();
        anyFailed = false;
        foreach (Dictionary<string, DotnetTestResult> cases in results.Values)
        {
            foreach (DotnetTestResult result in cases.Values)
            {
                anyFailed |= result.Outcome == DotnetTestOutcome.Failed;
                if (result.Outcome == DotnetTestOutcome.Failed && result.At is { } at)
                    now.Add(new HexProblem(at.File, at.Line, 0, context.Strings.Said("tests.problem", ("test", ShortName(result)), ("message", OneLine(result.Message))), false));
            }
        }

        if (now.Count == 0 && failures.Count == 0)
            return;

        failures = now;
        context.Panels.RefreshProblems();
    }

    // A result's name without its namespace, its class's name and the rest, ColourTests.Mixes and a case's arguments
    private static string ShortName(DotnetTestResult result)
    {
        string framework = result.Framework.Length > 0 ? " (" + result.Framework + ")" : string.Empty;
        if (result.Class.Length == 0 || !result.FullName.StartsWith(result.Class + ".", StringComparison.Ordinal))
            return result.FullName + framework;

        return DotnetTests.SplitClass(result.Class).Class + result.FullName[result.Class.Length..] + framework;
    }

    // A message of lines as one line, its runs of spaces one space, cut short past so long
    private static string OneLine(string message)
    {
        string line = string.Join(' ', message.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return line.Length > LONGEST_PROBLEM ? line[..LONGEST_PROBLEM] + "..." : line;
    }
}
