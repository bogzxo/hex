using System.Xml.Linq;

using Hex.Core.Projects;

namespace Hex.Tests;

public class DotnetTestsSearchTests
{
    [Fact]
    public void TheSearchTakesWordsAndOutcomes()
    {
        DotnetTestQuery query = DotnetTestQuery.Read("  colour Outcome:Failed is:\"not run\" mixes ", DotnetTestOutcomes.Passed);

        Assert.Equal(["colour", "mixes"], query.Words);
        Assert.Equal(DotnetTestOutcomes.Failed | DotnetTestOutcomes.NotRun | DotnetTestOutcomes.Passed, query.Outcomes);
        Assert.True(query.Names("Horizon.Tests.ColourTests.Mixes"));
        Assert.False(query.Names("Horizon.Tests.ColourTests.Blends"));
        Assert.True(query.Shows(DotnetTestOutcome.Failed));
        Assert.True(query.Shows(DotnetTestOutcome.NotRun));
        Assert.False(query.Shows(DotnetTestOutcome.Skipped));
    }

    [Fact]
    public void NothingTypedIsEveryTest()
    {
        DotnetTestQuery query = DotnetTestQuery.Read("   ", DotnetTestOutcomes.None);

        Assert.True(query.IsEverything);
        Assert.True(query.Names("Anything.At.All"));
        Assert.True(query.Shows(DotnetTestOutcome.Skipped));
        Assert.False(DotnetTestQuery.Read(string.Empty, DotnetTestOutcomes.Failed).IsEverything);
    }

    [Fact]
    public void CasesAreCountedTheWayVisualStudioCountsThem()
    {
        var fact = new DotnetTest("p", "N", "C", "Fact", "f.cs", 1);
        var theory = new DotnetTest("p", "N", "C", "Theory", "f.cs", 5) { Cases = 3 };
        var unrun = new DotnetTest("p", "N", "C", "Unrun", "f.cs", 9) { Cases = 2 };
        var results = new Dictionary<string, List<DotnetTestResult>>
        {
            ["N.C.Fact"] = [Result("N.C.Fact", DotnetTestOutcome.Passed)],
            ["N.C.Theory"] =
            [
                Result("N.C.Theory(x: 1)", DotnetTestOutcome.Passed),
                Result("N.C.Theory(x: 2)", DotnetTestOutcome.Passed),
                Result("N.C.Theory(x: 3)", DotnetTestOutcome.Failed),
                Result("N.C.Theory(x: 4)", DotnetTestOutcome.Skipped)
            ]
        };

        DotnetTestTally tally = DotnetTests.Tally([fact, theory, unrun], test => results.GetValueOrDefault(test.FullName) ?? []);

        // Four cases ran of the theory though its source said three (a MemberData can add more), the unrun one has two
        Assert.Equal(new DotnetTestTally(3, 7, 3, 1, 1), tally);
        Assert.Equal(2, tally.NotRun);
    }

    [Fact]
    public void ATheorysInlineCasesAreCountedOffTheSource()
    {
        const string source = """
            namespace N;

            public class C
            {
                [Theory]
                [InlineData(1)]
                [InlineData(2), InlineData(3)]
                public void Three(int x) { }

                [Fact]
                public void One() { }

                private void Helper() { }

                [Theory]
                [MemberData(nameof(Rows))]
                public void FromMembers(int x) { }
            }
            """;

        List<DotnetTest> found = DotnetTests.Find("p.csproj", "C.cs", source);

        Assert.Equal([3, 0, 0], found.Select(test => test.Cases));
    }

    [Fact]
    public void ACaseWithAQuoteGoesByAllOfItsNameWhereQuotesCanGo()
    {
        const string name = "N.C.Off(value: \"off\", on: True)";

        Assert.Equal("DisplayName~N.C.Off\\(value:", DotnetTests.Filter([name]));
        Assert.Equal("DisplayName=N.C.Off\\(value: \"off\", on: True\\)", DotnetTests.Filter([name], quotes: true));

        XElement settings = XElement.Parse(DotnetTests.RunSettings(DotnetTests.Filter([name], quotes: true)));
        Assert.Equal("DisplayName=N.C.Off\\(value: \"off\", on: True\\)", settings.Element("RunConfiguration")?.Element("TestCaseFilter")?.Value);
    }

    private static DotnetTestResult Result(string name, DotnetTestOutcome outcome) => new(name, outcome, TimeSpan.Zero, string.Empty, string.Empty, string.Empty);
}
