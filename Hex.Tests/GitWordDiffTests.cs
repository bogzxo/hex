using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>The words a diff changed inside the lines it changed, each line taken out against the one put in for it.</summary>
public sealed class GitWordDiffTests
{
    [Fact]
    public void One_changed_token_is_all_that_is_lit()
    {
        var (gone, came) = GitWordDiff.Changes("port 8080", "port 9090");

        Assert.Equal([(5, 4)], gone);
        Assert.Equal([(5, 4)], came);
    }

    [Fact]
    public void An_added_word_is_lit_on_the_new_line_only()
    {
        var (gone, came) = GitWordDiff.Changes("int port = 1;", "static int port = 1;");

        Assert.Empty(gone);
        Assert.Equal([(0, 7)], came);
    }

    [Fact]
    public void A_line_changed_right_through_is_lit_whole()
    {
        var (gone, came) = GitWordDiff.Changes("alpha", "omega");

        Assert.Equal([(0, 5)], gone);
        Assert.Equal([(0, 5)], came);
    }

    [Fact]
    public void A_long_line_goes_by_what_is_the_same_at_either_end()
    {
        string start = new('a', 300), end = new('b', 300);
        var (gone, came) = GitWordDiff.Changes(start + "one" + end, start + "four" + end);

        Assert.Equal([(300, 3)], gone);
        Assert.Equal([(300, 4)], came);
    }

    [Fact]
    public void A_diff_has_the_words_by_the_line_of_its_text()
    {
        GitDiffText diff = GitDiffText.Of("listen\nport 8080\nend\n", "listen\nport 9090\nend\n");

        Assert.Equal("listen\nport 8080\nport 9090\nend", diff.Text);
        Assert.Equal([new GitDiffWord(1, 5, 4, Added: false), new GitDiffWord(2, 5, 4, Added: true)], diff.Words);
    }

    [Fact]
    public void Lines_only_added_have_no_words()
    {
        Assert.Empty(GitDiffText.Of("one\n", "one\ntwo\n").Words);
    }
}
