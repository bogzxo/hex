namespace Hex.Core.Projects;

/// <summary>The outcomes the explorer is narrowed down to, none of them for every test.</summary>
[Flags]
internal enum DotnetTestOutcomes
{
    None = 0,
    Passed = 1,
    Failed = 2,
    Skipped = 4,
    NotRun = 8
}

/// <summary>
/// What the explorer's box narrows the tests down to, the words a name has to have in it (any case, each anywhere in the
/// namespace, the class, the method or a case's arguments) and the outcomes shown, the way Visual Studio's search box
/// goes. <c>Outcome:Failed</c> typed in it is the same as its Failed button.
/// </summary>
internal sealed record DotnetTestQuery(IReadOnlyList<string> Words, DotnetTestOutcomes Outcomes)
{
    /// <summary>Every test, nothing typed and no outcome picked.</summary>
    public static readonly DotnetTestQuery Everything = new([], DotnetTestOutcomes.None);

    /// <summary>Whether it shows every test there is.</summary>
    public bool IsEverything => Words.Count == 0 && Outcomes == DotnetTestOutcomes.None;

    /// <summary>Whether a name has every word typed in it.</summary>
    public bool Names(string name)
    {
        foreach (string word in Words)
        {
            if (!name.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>Whether an outcome is one of the ones shown.</summary>
    public bool Shows(DotnetTestOutcome outcome) => Outcomes == DotnetTestOutcomes.None || (Outcomes & Of(outcome)) != 0;

    /// <summary>The flag of an outcome.</summary>
    public static DotnetTestOutcomes Of(DotnetTestOutcome outcome) => outcome switch
    {
        DotnetTestOutcome.Passed => DotnetTestOutcomes.Passed,
        DotnetTestOutcome.Failed => DotnetTestOutcomes.Failed,
        DotnetTestOutcome.Skipped => DotnetTestOutcomes.Skipped,
        _ => DotnetTestOutcomes.NotRun
    };

    /// <summary>
    /// What's typed in the box read, its words and the outcomes it names (<c>Outcome:Failed</c>, <c>is:passed</c>,
    /// <c>outcome:"not run"</c>), with the outcomes picked by the buttons beside it as well.
    /// </summary>
    public static DotnetTestQuery Read(string typed, DotnetTestOutcomes picked)
    {
        var words = new List<string>();
        DotnetTestOutcomes outcomes = picked;
        string text = typed.Replace("\"not run\"", "notrun", StringComparison.OrdinalIgnoreCase).Replace("\"", string.Empty, StringComparison.Ordinal);
        foreach (string word in text.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = word.IndexOf(':');
            string key = colon > 0 ? word[..colon] : string.Empty;
            if (key.Equals("outcome", StringComparison.OrdinalIgnoreCase) || key.Equals("is", StringComparison.OrdinalIgnoreCase))
            {
                outcomes |= word[(colon + 1)..].ToLowerInvariant() switch
                {
                    "passed" or "pass" => DotnetTestOutcomes.Passed,
                    "failed" or "fail" => DotnetTestOutcomes.Failed,
                    "skipped" or "skip" => DotnetTestOutcomes.Skipped,
                    "notrun" or "not-run" or "none" => DotnetTestOutcomes.NotRun,
                    _ => DotnetTestOutcomes.None
                };
                continue;
            }

            words.Add(word);
        }

        return new DotnetTestQuery(words, outcomes);
    }
}

/// <summary>
/// The tests of some list counted the way Visual Studio counts them, by the method and by the case. A theory of three
/// cases is a test and three cases, and it's the cases that pass and fail, so three passed is three cases.
/// </summary>
internal readonly record struct DotnetTestTally(int Tests, int Cases, int Passed, int Failed, int Skipped)
{
    /// <summary>The cases with no result yet.</summary>
    public int NotRun => Math.Max(0, Cases - Passed - Failed - Skipped);
}

internal static partial class DotnetTests
{
    /// <summary>
    /// Some tests counted, each with its results. A test has as many cases as it has results, or as its source says it
    /// has (its InlineData) before it's run, one at the least.
    /// </summary>
    public static DotnetTestTally Tally(IEnumerable<DotnetTest> tests, Func<DotnetTest, IEnumerable<DotnetTestResult>> resultsOf)
    {
        int count = 0, cases = 0, passed = 0, failed = 0, skipped = 0;
        foreach (DotnetTest test in tests)
        {
            count++;
            int results = 0;
            foreach (DotnetTestResult result in resultsOf(test))
            {
                results++;
                switch (result.Outcome)
                {
                    case DotnetTestOutcome.Passed: passed++; break;
                    case DotnetTestOutcome.Failed: failed++; break;
                    case DotnetTestOutcome.Skipped: skipped++; break;
                }
            }

            cases += Math.Max(1, Math.Max(results, test.Cases));
        }

        return new DotnetTestTally(count, cases, passed, failed, skipped);
    }
}
