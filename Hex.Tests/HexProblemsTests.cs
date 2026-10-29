using Hex.Dawn;

namespace Hex.Tests;

public sealed class HexProblemsTests
{
    private static readonly HexProblem[] Problems =
    [
        new("src/first.cs", 0, 1, "Unused value (CS0219)", true),
        new("src/second.cs", 2, 3, "Simplify expression", true, true),
        new("src/second.cs", 4, 5, "Missing semicolon (CS1002)", false),
        new("src/first.cs", 6, 7, "Unknown name (CS0103)", false),
    ];

    [Fact]
    public void Groups_are_ordered_by_severity_and_preserve_file_priority()
    {
        var groups = DawnScene.FilterProblems(Problems, "");
        Assert.Equal(new[] { 0, 1, 2 }, groups.Select(group => group.Key));
        Assert.Equal(new[] { Problems[2], Problems[3] }, groups[0]);
        Assert.Equal(Problems, groups.SelectMany(group => group).OrderBy(problem => Array.IndexOf(Problems, problem)));
    }

    [Theory]
    [InlineData("  cs1002  ", 2)]
    [InlineData("SIMPLIFY", 1)]
    [InlineData("unused VALUE", 0)]
    public void Search_matches_message_and_code_without_case_or_surrounding_space(string query, int index)
    {
        Assert.Equal(Problems[index], Assert.Single(Assert.Single(DawnScene.FilterProblems(Problems, query))));
    }

    [Fact]
    public void Search_matches_paths_with_either_separator_and_does_not_change_source()
    {
        var groups = DawnScene.FilterProblems(Problems, @"SRC\SECOND.CS");
        Assert.Equal(new[] { Problems[2], Problems[1] }, groups.SelectMany(group => group));
        Assert.Empty(DawnScene.FilterProblems(Problems, "nothing matches"));
        Assert.Equal(4, DawnScene.FilterProblems(Problems, "   ").Sum(group => group.Count()));
    }

    [Fact]
    public void Hints_keep_their_severity_even_without_the_warning_flag()
    {
        var hint = new HexProblem("file.cs", 0, 0, "Hint", false, true);
        Assert.Equal(2, Assert.Single(DawnScene.FilterProblems([hint], "")).Key);
        Assert.Equal("accent", hint.Shade);
        Assert.Equal(new[] { "warning", "accent", "error", "error" }, Problems.Select(problem => problem.Shade));
    }
}
