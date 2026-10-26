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
    public void The_files_are_every_hor_but_what_gets_built_git_and_submodules_dot_folders_included()
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

        Assert.Equal([".hidden/e.hor", "a.hor", "ui/b.hor"], files.Select(file => HexSearch.RelativeTo([root], file)));
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
    public void A_regex_that_doesnt_build_says_why()
    {
        Write("a.hor", "let a = (1);");

        HexSearchOutcome outcome = HexSearch.Search([root], "(", new UIFindOptions(Regex: true));

        Assert.Empty(outcome.Found);
        Assert.False(string.IsNullOrEmpty(outcome.Error));
        Assert.Null(HexSearch.Search([root], @"\(", new UIFindOptions(Regex: true)).Error);
    }

    [Fact]
    public void Every_match_on_a_line_is_a_result_of_its_own()
    {
        Write("a.txt", "    foo foo foo\nbar foo");

        List<HexResult> found = HexSearch.FindIn([root], "foo", new UIFindOptions());

        Assert.Equal(4, found.Count);
        Assert.Equal([(0, 4), (0, 8), (0, 12), (1, 4)], found.Select(result => (result.Line, result.Column)));
        Assert.Equal([0, 4, 8], found.Take(3).Select(result => result.FoundAt));
        Assert.All(found, result => Assert.Equal(3, result.Found));
        Assert.Equal([(4, 3), (8, 3)], HexSearch.AllMatches("bar foo foo", "foo", new UIFindOptions(), null));
    }

    [Fact]
    public void The_files_to_include_and_exclude_narrow_the_search()
    {
        Write("src/a.cs", "needle");
        Write("src/notes.md", "needle");
        Write("src/deep/b.cs", "needle");
        Write("docs/c.md", "needle");
        Write("top.cs", "needle");
        List<string> Found(string include, string exclude) =>
            [.. HexSearch.Search([root], "needle", new UIFindOptions(), include, exclude).Found.Select(result => HexSearch.RelativeTo([root], result.Path)).Order()];

        Assert.Equal(5, Found(string.Empty, string.Empty).Count);
        Assert.Equal(["src/a.cs", "src/deep/b.cs", "src/notes.md"], Found("src/**", string.Empty));
        Assert.Equal(["src/a.cs", "src/deep/b.cs"], Found("src/**", "*.md"));
        Assert.Equal(["src/a.cs", "src/deep/b.cs", "top.cs"], Found(string.Empty, "*.md"));
        Assert.Equal(["src/a.cs", "src/notes.md", "top.cs"], Found("src, top.cs", "deep"));
        Assert.Equal(["docs/c.md", "src/notes.md", "top.cs"], Found("*.{md,txt}, top.cs", string.Empty));
        Assert.Equal(["*.{md,txt}", "top.cs"], HexScope.Split("*.{md,txt}, top.cs,"));
    }

    [Fact]
    public void Replacing_in_files_puts_the_groups_in_and_keeps_the_bom_and_crlf()
    {
        string path = Path.Combine(root, "a.txt");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(path, [.. bom, .. System.Text.Encoding.UTF8.GetBytes("let a = 1; let b = 22;\r\nlet c = 3;\r\n")]);
        var options = new UIFindOptions(Regex: true);
        List<HexResult> found = HexSearch.FindIn([root], @"(\w) = (\d+)", options);
        Assert.Equal(3, found.Count);

        // The match of c dismissed from the list, it stays as it is
        List<HexReplaced> done = HexSearch.ReplaceIn(found.Where(result => result.Line == 0), @"(\w) = (\d+)", "$2 = $1", options);

        HexReplaced only = Assert.Single(done);
        Assert.Equal(2, only.Count);
        byte[] written = File.ReadAllBytes(path);
        Assert.Equal(bom, written[..3]);
        Assert.Equal("let 1 = a; let 22 = b;\r\nlet c = 3;\r\n", System.Text.Encoding.UTF8.GetString(written[3..]));
    }

    [Fact]
    public void Replacing_in_an_open_file_goes_through_the_editor_and_leaves_the_disk_alone()
    {
        string path = Write("a.txt", "foo foo\n");
        List<HexResult> found = HexSearch.FindIn([root], "foo", new UIFindOptions());
        string? rewritten = null;

        List<HexReplaced> done = HexSearch.ReplaceIn(found, "foo", "bar", new UIFindOptions(), _ => "foo foo\nfoo\n", (_, text) => rewritten = text);

        Assert.Equal(2, Assert.Single(done).Count);
        Assert.Equal("bar bar\nfoo\n", rewritten);
        Assert.Equal("foo foo\n", File.ReadAllText(path));
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
