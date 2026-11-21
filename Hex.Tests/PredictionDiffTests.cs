using Hex.Core;

namespace Hex.Tests;

/// <summary>A model's old text against its new as the fewest edits, narrowed to the characters that differ.</summary>
public sealed class PredictionDiffTests
{
    private static string Applied(string old, IReadOnlyList<PredictionEdit> edits, int offset = 0)
    {
        string text = old;
        foreach (PredictionEdit edit in edits.Reverse())
            text = text[..(edit.Start - offset)] + edit.Text + text[(edit.End - offset)..];
        return text;
    }

    [Fact]
    public void An_insertion_is_one_edit_of_what_was_put_in()
    {
        var edits = PredictionDiff.Edits("int x;\nint y;", "int x = 1;\nint y;", 0);

        Assert.Equal(new PredictionEdit(5, 5, " = 1"), Assert.Single(edits));
    }

    [Fact]
    public void A_deletion_is_one_edit_with_no_text()
    {
        var edits = PredictionDiff.Edits("a\nb\nc", "a\nc", 0);

        Assert.Equal("a\nc", Applied("a\nb\nc", edits));
        Assert.Equal("", Assert.Single(edits).Text);
    }

    [Fact]
    public void A_replacement_is_narrowed_to_the_characters_that_differ_and_moved_by_the_offset()
    {
        var edits = PredictionDiff.Edits("var total = Sum(a);", "var total = Sum(b);", 100);

        Assert.Equal(new PredictionEdit(116, 117, "b"), Assert.Single(edits));
    }

    [Fact]
    public void Two_changes_apart_are_two_edits()
    {
        string old = "Foo(1);\nkeep\nkeep\nkeep\nFoo(2);", updated = "Bar(1);\nkeep\nkeep\nkeep\nBar(2);";

        var edits = PredictionDiff.Edits(old, updated, 0);

        Assert.Equal(2, edits.Count);
        Assert.Equal(updated, Applied(old, edits));
    }

    [Fact]
    public void A_change_in_whitespace_only_is_still_an_edit_but_none_for_the_same_text()
    {
        Assert.Empty(PredictionDiff.Edits("same\ntext", "same\ntext", 0));
        Assert.Equal(new PredictionEdit(0, 0, "    "), Assert.Single(PredictionDiff.Edits("x", "    x", 0)));
    }

    [Theory]
    [InlineData("a\nb", "a\nb\nc")]
    [InlineData("a\nb", "a")]
    [InlineData("a", "a\n")]
    [InlineData("a\n", "a")]
    [InlineData("", "x\ny")]
    [InlineData("x\ny", "")]
    [InlineData("one\ntwo\nthree", "zero\none\ntwo\nthree\nfour")]
    public void Lines_at_the_ends_come_out_right(string old, string updated)
    {
        var edits = PredictionDiff.Edits(old, updated, 0);

        Assert.Equal(updated, Applied(old, edits));
        Assert.All(edits, edit => Assert.InRange(edit.End, edit.Start, old.Length));
    }

    [Fact]
    public void Ghost_text_is_one_insertion_at_the_caret_or_a_rewrite_of_the_rest_of_its_line_that_keeps_it()
    {
        string text = "Run(\nnext";

        Assert.True(PredictionDiff.IsGhost([new PredictionEdit(4, 4, "x)")], text, 4));
        Assert.True(PredictionDiff.IsGhost([new PredictionEdit(3, 4, "(x)")], "Run(\n", 3));
        Assert.False(PredictionDiff.IsGhost([new PredictionEdit(3, 4, "[x]")], "Run(\n", 3));
        Assert.False(PredictionDiff.IsGhost([new PredictionEdit(0, 3, "Go")], text, 4));
        Assert.False(PredictionDiff.IsGhost([new PredictionEdit(4, 4, "x"), new PredictionEdit(6, 6, "y")], text, 4));
    }
}
