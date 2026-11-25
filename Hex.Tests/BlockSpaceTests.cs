using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>The gap the code editor keeps open for a box (AI Edit): its row maths and how it moves with the lines.</summary>
public sealed class BlockSpaceTests
{
    [Theory]
    [InlineData(1.0f, 20.0f, 1)]
    [InlineData(0.0f, 20.0f, 1)]
    [InlineData(20.0f, 20.0f, 1)]
    [InlineData(20.5f, 20.0f, 2)]
    [InlineData(114.0f, 18.0f, 7)]
    [InlineData(40.0f, 0.0f, 1)]
    public void A_gap_takes_the_whole_rows_that_hold_its_height(float height, float lineHeight, int rows) =>
        Assert.Equal(rows, BlockSpaceMath.RowsFor(height, lineHeight));

    [Theory]
    // lines put in above the line push it down, those under it don't
    [InlineData(5, 2, 3, false, 20, 8)]
    [InlineData(5, 7, 3, false, 20, 5)]
    // an enter before the line's text takes the line, and its gap, down with it
    [InlineData(4, 4, 1, true, 20, 5)]
    [InlineData(4, 4, 1, false, 20, 4)]
    // lines taken out above move it up, those that held it join the line they start on
    [InlineData(10, 4, -3, false, 20, 7)]
    [InlineData(5, 6, -3, false, 20, 3)]
    // never past the lines there are, line 0 stays line 0
    [InlineData(19, 2, -5, false, 12, 11)]
    [InlineData(0, 3, 2, false, 20, 0)]
    public void A_gap_moves_with_its_line(int line, int at, int by, bool blank, int lines, int expected) =>
        Assert.Equal(expected, BlockSpaceMath.MoveLine(line, at, by, blank, lines));

    [Theory]
    // the view still where the gap's reveal took it: back to where it was
    [InlineData(60.0f, 60.0f, 0.0f, 0.0f)]
    [InlineData(60.5f, 60.0f, 0.0f, 0.0f)]
    [InlineData(60.0f, 60.0f, 36.0f, 36.0f)]
    // the person scrolled elsewhere meanwhile: left alone (-1 for none)
    [InlineData(200.0f, 60.0f, 0.0f, -1.0f)]
    // it never moved from where it was
    [InlineData(0.0f, 0.0f, 0.0f, -1.0f)]
    public void The_scroll_a_gap_caused_goes_with_it(float current, float revealedTo, float origin, float expected) =>
        Assert.Equal(expected < 0 ? (float?)null : expected, BlockSpaceMath.ScrollAfterGap(current, revealedTo, origin));

    [Fact]
    public void Setting_a_gap_again_with_the_same_things_changes_nothing_and_clearing_removes_it()
    {
        var editor = new CodeEditor { Text = "one\ntwo\nthree" };
        editor.SetBlockSpace("box", 0, above: true, 60.0f);
        editor.SetBlockSpace("box", 0, above: true, 60.0f);
        UIBlockSpace gap = Assert.Single(editor.BlockSpaces);
        Assert.Equal(new UIBlockSpace("box", 0, true, 60.0f), gap);

        // One gap an owner: another place replaces it
        editor.SetBlockSpace("box", 2, above: false, 80.0f);
        Assert.Equal(new UIBlockSpace("box", 2, false, 80.0f), Assert.Single(editor.BlockSpaces));

        editor.SetBlockSpace("other", 1, above: true, 10.0f);
        editor.ClearBlockSpace("box");
        Assert.Equal("other", Assert.Single(editor.BlockSpaces).Owner);
        editor.ClearBlockSpace("other");
        Assert.Empty(editor.BlockSpaces);
    }

    [Fact]
    public void A_gap_is_anchored_to_its_line_as_lines_go_in_above_it()
    {
        var editor = new CodeEditor { Text = "one\ntwo\nthree\nfour" };
        editor.SetBlockSpace("box", 2, above: true, 40.0f);

        // A line put in at the top pushes line 2 to 3 and the gap with it
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("zero\n");
        Assert.Equal(3, Assert.Single(editor.BlockSpaces).Line);

        // Typing on a line under it doesn't move it
        editor.Document.MoveTo(new UITextPosition(4, 0));
        editor.Document.Insert("x");
        Assert.Equal(3, Assert.Single(editor.BlockSpaces).Line);
    }
}
