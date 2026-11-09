using Hex.Core.Projects;

namespace Hex.Tests;

public class DotnetTestsTests
{
    [Fact]
    public void FailureNavigationPrefersWorkspaceFramesAndPreservesFallbacks()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex failure navigation " + Guid.NewGuid());
        string workspace = Path.Combine(root, "project"), outside = Path.Combine(root, "project-external");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        try
        {
            string helper = Path.Combine(outside, "Assertions.cs"), source = Path.Combine(workspace, "Async tests.cs");
            File.WriteAllText(helper, "// external helper");
            File.WriteAllText(source, "// workspace test");
            string external = $"   at Assertions.Fail() in {helper}:line 9\n";
            string own = $"   at Tests.AsyncTest.MoveNext() in {source}:line 42\n";
            string missing = $"   at Remote.Fail() in {Path.Combine(root, "missing.cs")}:line 2\n";
            Assert.Equal((source, 41), DotnetTests.FailedAt(missing + external + own, [workspace]));
            Assert.Equal((helper, 8), DotnetTests.FailedAt(external + own));
            Assert.Equal((helper, 8), DotnetTests.FailedAt(missing + external, [workspace]));
            Assert.Equal((source, 41), DotnetTests.FailedAt(own + external, [workspace]));
            Assert.Null(DotnetTests.FailedAt(missing, [workspace]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("", "error output", "error output")]
    [InlineData("normal output", "", "normal output")]
    [InlineData("normal output", "error output", "normal output\nerror output")]
    [InlineData("", "", "")]
    public void TrxKeepsBothOutputStreamsPerParameterizedCase(string stdout, string stderr, string expected)
    {
        var ns = (System.Xml.Linq.XNamespace)"http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        System.Xml.Linq.XElement Result(string name, string standard, string error) => new(ns + "UnitTestResult",
            new System.Xml.Linq.XAttribute("testName", name), new System.Xml.Linq.XAttribute("outcome", "Passed"),
            new System.Xml.Linq.XElement(ns + "Output", new System.Xml.Linq.XElement(ns + "StdOut", standard),
                new System.Xml.Linq.XElement(ns + "StdErr", error)));
        var trx = new System.Xml.Linq.XElement(ns + "TestRun", new System.Xml.Linq.XElement(ns + "Results",
            Result("Tests.Sample.Case(value: 1)", stdout, stderr), Result("Tests.Sample.Case(value: 2)", "other case", "other error")));
        var results = DotnetTests.ReadTrx(trx.ToString());
        Assert.Equal(2, results.Count);
        Assert.Equal(expected, results[0].Output);
        Assert.Equal("Tests.Sample.Case(value: 1)", results[0].FullName);
        Assert.Equal("other case\nother error", results[1].Output);
        Assert.Equal("Tests.Sample.Case(value: 2)", results[1].FullName);
    }

    [Fact]
    public void Selected_solution_excludes_nested_copies_and_includes_external_projects()
    {
        string folder = Directory.CreateTempSubdirectory("hex-test-scope-").FullName;
        try
        {
            string root = Path.Combine(folder, "workspace"), external = Path.Combine(folder, "external");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(external);
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            const string projectText = "<Project><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>";
            string selected = Path.Combine(root, "Tests.csproj"), duplicate = Path.Combine(root, "nested", "Tests.csproj"), linked = Path.Combine(external, "Linked.csproj");
            foreach (string project in new[] { selected, duplicate, linked }) File.WriteAllText(project, projectText);
            string solution = Path.Combine(root, "Workspace.slnx");
            File.WriteAllText(solution, "<Solution><Project Path=\"Tests.csproj\" /><Project Path=\"../external/Linked.csproj\" /></Solution>");
            Assert.Equal(new[] { selected, linked }.Order(), DotnetTests.TestProjects([root], solution).Order());
            Assert.Equal(new[] { selected, duplicate }.Order(), DotnetTests.TestProjects([root]).Order());
            Assert.Empty(DotnetTests.TestProjects([root], Path.Combine(root, "missing.slnx")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FindsXunitTestsWithTheirClassAndNamespace()
    {
        const string source = """
            namespace Horizon.Tests.Shaders;

            public record Case(int A);

            public class ShaderTests
            {
                [Fact]
                public void Compiles() { }

                [Theory]
                [InlineData(1)]
                public async Task RunsWith(int x) { await Task.Yield(); }

                private void NotATest() { }
            }

            public class Other
            {
                [Fact] public void Second() { }
            }
            """;

        List<DotnetTest> tests = DotnetTests.Find("p.csproj", "f.cs", source);

        Assert.Equal(["Horizon.Tests.Shaders.ShaderTests.Compiles", "Horizon.Tests.Shaders.ShaderTests.RunsWith", "Horizon.Tests.Shaders.Other.Second"],
            tests.Select(test => test.FullName));
        Assert.Equal(7, tests[0].Line);
    }

    [Fact]
    public void BlockNamespacesAndNUnitAttributesCount()
    {
        const string source = """
            namespace A.B
            {
                class Outer
                {
                    [Test]
                    public void One() { }

                    [TestCase(1)]
                    public void Two(int x) { }
                }
            }
            """;

        Assert.Equal(["A.B.Outer.One", "A.B.Outer.Two"], DotnetTests.Find("p", "f", source).Select(test => test.FullName));
    }

    [Fact]
    public void WhatsInStringsAndCommentsIsntCode()
    {
        const string source = """"
            namespace Horizon.Tests.UI;

            public sealed class ColouringAgainTests
            {
                private const string Fixture = """
                    namespace Big;
                    class Fake { [Fact] public void NotOne() { } }
                    """;

                /* namespace Nope; class Gone { [Fact] public void Hidden() { } } */
                private static readonly string Verbatim = @"namespace ""Nope""; class Gone {";
                private static readonly string Holes = $"{(true ? "}" : "{")} class Gone {{";
                private static readonly char Brace = '{';

                [Fact]
                public void Colours() { }
            }
            """";

        List<DotnetTest> tests = DotnetTests.Find("p", "f", source);

        Assert.Equal(["Horizon.Tests.UI.ColouringAgainTests.Colours"], tests.Select(test => test.FullName));
        Assert.Equal(15, tests[0].Line);
    }

    [Fact]
    public void NestedClassesGenericsAndAttributesOnOneLine()
    {
        const string source = """
            namespace A
            {
                namespace B
                {
                    public class Outer<T> where T : class
                    {
                        public record Row(int X);

                        [Fact] public void First() { } [Fact] public async Task Second() { await Task.Yield(); }

                        public class Inner
                        {
                            [Theory, InlineData(1)]
                            public void Generic<U>(int x) { }
                        }

                        [Fact]
                        public void Third() { }
                    }
                }
            }
            """;

        Assert.Equal(["A.B.Outer.First", "A.B.Outer.Second", "A.B.Outer+Inner.Generic", "A.B.Outer.Third"],
            DotnetTests.Find("p", "f", source).Select(test => test.FullName));
    }

    [Fact]
    public void ReadsTrxResults()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="N.C.Good" outcome="Passed" duration="00:00:00.0120000" />
                <UnitTestResult testId="2" testName="N.C.Bad" outcome="Failed" duration="00:00:01.5000000">
                  <Output><ErrorInfo><Message>Assert.Equal() Failure</Message><StackTrace>at N.C.Bad() in C:\x\C.cs:line 12</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
                <UnitTestResult testId="3" testName="N.C.Case(x: 1)" outcome="NotExecuted" duration="00:00:00" />
              </Results>
              <TestDefinitions>
                <UnitTest id="1" name="N.C.Good"><TestMethod className="N.C" name="Good" /></UnitTest>
                <UnitTest id="2" name="N.C.Bad"><TestMethod className="N.C" name="Bad" /></UnitTest>
                <UnitTest id="3" name="N.C.Case(x: 1)"><TestMethod className="N.C" name="Case" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        List<DotnetTestResult> results = DotnetTests.ReadTrx(trx);

        Assert.Equal(3, results.Count);
        Assert.Equal(("N.C.Good", DotnetTestOutcome.Passed), (results[0].FullName, results[0].Outcome));
        Assert.Equal(DotnetTestOutcome.Failed, results[1].Outcome);
        Assert.Equal("Assert.Equal() Failure", results[1].Message);
        Assert.Equal(TimeSpan.FromSeconds(1.5), results[1].Duration);
        Assert.Equal("N.C.Case(x: 1)", results[2].FullName);
        Assert.Equal("N.C.Case", DotnetTests.WithoutArguments(results[2].FullName));
        Assert.Equal(DotnetTestOutcome.Skipped, results[2].Outcome);
        Assert.Equal("N.C", results[2].Class);
        Assert.True(results[2].IsCase);
        Assert.Equal("N.C.Case", results[2].Test);
    }

    [Fact]
    public void WhatARunSaysIsThereIsPutUnderItsRealClass()
    {
        List<DotnetTest> known =
        [
            new("p", "Horizon.Tests.UI", "Seen", "One", "Seen.cs", 4),
            new("p", "Big", "ColouringAgainTests", "Colours", "ColouringAgainTests.cs", 9)
        ];
        DotnetTestResult[] ran =
        [
            new("Horizon.Tests.UI.Seen.One", DotnetTestOutcome.Passed, TimeSpan.Zero, "", "", "") { Class = "Horizon.Tests.UI.Seen" },
            new("Horizon.Tests.UI.Seen.Two(x: 1)", DotnetTestOutcome.Failed, TimeSpan.Zero, "", "", "") { Class = "Horizon.Tests.UI.Seen", At = ("Seen.cs", 12) },
            new("Horizon.Tests.UI.ColouringAgainTests.Colours", DotnetTestOutcome.Passed, TimeSpan.Zero, "", "", "") { Class = "Horizon.Tests.UI.ColouringAgainTests" },
            new("Horizon.Tests.UI.Outer+Inner.Deep", DotnetTestOutcome.Passed, TimeSpan.Zero, "", "", "") { Class = "Horizon.Tests.UI.Outer+Inner" }
        ];

        List<DotnetTest> reconciled = DotnetTests.Reconcile("p", known, ran)!;

        Assert.Equal(["Horizon.Tests.UI.Seen.One", "Horizon.Tests.UI.ColouringAgainTests.Colours", "Horizon.Tests.UI.Seen.Two", "Horizon.Tests.UI.Outer+Inner.Deep"],
            reconciled.Select(test => test.FullName));
        Assert.Equal(("ColouringAgainTests.cs", 9), (reconciled[1].File, reconciled[1].Line));
        Assert.Equal(("Seen.cs", 12), (reconciled[2].File, reconciled[2].Line));
        Assert.Null(DotnetTests.Reconcile("p", reconciled, ran));
    }

    [Fact]
    public void FiltersEscapeWhatTheFilterReads()
    {
        Assert.Equal("FullyQualifiedName=A.B|DisplayName=A.C\\(x: 1\\)", DotnetTests.Filter(["A.B", "A.C(x: 1)", "A.B"]));
        Assert.Equal("DisplayName~A.C\\(s:", DotnetTests.Filter(["A.C(s: \"off\")"]));
    }

    [Fact]
    public void FiltersEscapeWhatTheFilterReadsWithoutMatchingLongerMethodNames()
    {
        Assert.Equal("FullyQualifiedName=A.Tests.One", DotnetTests.Filter(["A.Tests.One"]));
        Assert.Equal("FullyQualifiedName~A.Tests.", DotnetTests.Filter(["A.Tests."]));
        Assert.Equal("DisplayName=A.Tests.Theory\\(text: \"one\"\\)",
            DotnetTests.Filter(["A.Tests.Theory(text: \"one\")"], quotes: true));
    }

    [Fact]
    public void TestProjectsAreTheOnesThatReferenceAFramework()
    {
        Assert.True(DotnetTests.IsTestProject("""<PackageReference Include="xunit" Version="2.9.3" />"""));
        Assert.True(DotnetTests.IsTestProject("""<PackageReference Include="Microsoft.NET.Test.Sdk" />"""));
        Assert.False(DotnetTests.IsTestProject("""<PackageReference Include="Silk.NET.Vulkan" />"""));
    }
}
