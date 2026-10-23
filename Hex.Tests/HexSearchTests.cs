using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// Finding in the files of a workspace (Dawn's HexSearch), on a folder made for each test: which files are looked
/// through, where one is inside of the folders, what is found as the find bar's toggles say, and what doesn't parse.
/// </summary>
public sealed class HexSearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hex-search-" + Guid.NewGuid().ToString("N"));

    public HexSearchTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void The_files_are_every_hor_but_what_gets_built_git_and_submodules()
    {
        Write("a.hor", "let a = 1;");
        Write("ui/b.hor", "let b = 2;");
        Write("notes.txt", "not a layout");
        Write("bin/Debug/c.hor", "let c = 3;");
        Write("obj/d.hor", "let d = 4;");
        Write(".hidden/e.hor", "let e = 5;");
        Write("engine/.git", "gitdir: ../.git/modules/engine");
        Write("engine/f.hor", "let f = 6;");
        Write("~syncthing~g.hor", "let g = 7;");

        List<string> files = HexSearch.FilesUnder([root]);

        Assert.Equal(["a.hor", "ui/b.hor"], files.Select(file => HexSearch.RelativeTo([root], file)));
    }

    [Fact]
    public void A_file_is_said_by_where_it_is_and_by_its_folder_with_more_than_one()
    {
        string other = Path.Combine(root, "other");
        string file = Write("other/x.hor", string.Empty);

        Assert.Equal("other/x.hor", HexSearch.RelativeTo([root], file));
        Assert.Equal("other/x.hor", HexSearch.RelativeTo([root, other], file));
        Assert.Equal("x.hor", HexSearch.RelativeTo([other], file));
    }

    [Fact]
    public void Finding_goes_by_the_case_whole_words_and_a_regex_as_asked()
    {
        Write("a.hor", "let Button = 1;\nlet buttons = 2;\nlet other = 3;");

        Assert.Equal(2, HexSearch.FindIn([root], "button", new UIFindOptions()).Count);
        Assert.Single(HexSearch.FindIn([root], "button", new UIFindOptions(MatchCase: true)));
        Assert.Single(HexSearch.FindIn([root], "button", new UIFindOptions(WholeWord: true)));

        List<HexResult> found = HexSearch.FindIn([root], @"o\w+r", new UIFindOptions(Regex: true));
        HexResult only = Assert.Single(found);
        Assert.Equal((2, 4), (only.Line, only.Column));
        Assert.StartsWith("a.hor:3", only.Text);
    }

    [Fact]
    public void A_regex_that_doesnt_build_finds_nothing()
    {
        Write("a.hor", "let a = (1);");
        Assert.Empty(HexSearch.FindIn([root], "(", new UIFindOptions(Regex: true)));
    }

    [Fact]
    public void Checking_says_what_doesnt_parse_and_where()
    {
        Write("good.hor", "let a = 1;");
        Write("bad.hor", "let a = 1;\nlet = ;");

        var (files, problems) = HexSearch.CheckFiles([root]);

        Assert.Equal(2, files);
        HexResult problem = Assert.Single(problems);
        Assert.EndsWith("bad.hor", problem.Path);
        Assert.Equal(1, problem.Line);
    }

    [Fact]
    public void The_toggles_are_said_after_what_is_asked()
    {
        Assert.Equal(string.Empty, HexSearch.DescribeOptions(new UIFindOptions()));
        Assert.Equal(", the case matching, as a regex (the find bar's toggles)", HexSearch.DescribeOptions(new UIFindOptions(MatchCase: true, Regex: true)));
    }

    [Theory]
    [InlineData("    writeln(\"area \", box.area);", 13, true)]
    [InlineData("    writeln(\"area \", box.area);", 25, false)]
    [InlineData("    int area = 2; // the area", 8, false)]
    [InlineData("    int area = 2; // the area", 26, true)]
    [InlineData("    /* area */ int area;", 7, true)]
    [InlineData("    /* area */ int area;", 19, false)]
    [InlineData("    auto c = 'a'; area();", 18, false)]
    [InlineData("    auto s = \"say \\\"area\\\"\"; area;", 20, true)]
    public void A_place_in_a_text_or_a_comment_is_known_from_one_in_the_code(string line, int column, bool inside) =>
        Assert.Equal(inside, HexSearch.InTextOrComment(line, column));
}
