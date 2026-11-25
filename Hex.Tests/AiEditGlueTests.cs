using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>The parts of AI edits' glue that need no editor: quick actions, recent instructions, changed line counts, the progress line.</summary>
public sealed class AiEditGlueTests
{
    [Fact]
    public void Every_quick_action_asks_something_and_an_unknown_one_nothing()
    {
        foreach (string action in HexAiEditGlue.QuickActions)
            Assert.NotEmpty(HexAiEditGlue.Instruction(action));
        Assert.Equal(string.Empty, HexAiEditGlue.Instruction("nope"));
    }

    [Fact]
    public void A_new_instruction_goes_first_and_an_old_copy_goes()
    {
        List<string> recent = HexAiEditGlue.Remember(["b", "a"], "  A ");
        Assert.Equal(["A", "b"], recent);
    }

    [Fact]
    public void An_empty_instruction_changes_nothing() =>
        Assert.Equal(["a"], HexAiEditGlue.Remember(["a"], "   "));

    [Fact]
    public void Recent_instructions_are_kept_to_twenty()
    {
        List<string> recent = [];
        for (int i = 0; i < 30; i++)
            recent = HexAiEditGlue.Remember(recent, "do " + i);
        Assert.Equal(HexAiEditGlue.MOST_RECENT, recent.Count);
        Assert.Equal("do 29", recent[0]);
    }

    [Theory]
    [InlineData(-1, -1, 3, 0)]
    [InlineData(0, -1, 3, 1)]
    [InlineData(2, -1, 3, 2)]
    [InlineData(1, 1, 3, 0)]
    [InlineData(0, 1, 3, -1)]
    [InlineData(-1, 1, 3, -1)]
    [InlineData(-1, -1, 0, -1)]
    public void Up_and_Down_walk_the_recent_instructions(int at, int key, int count, int expected) =>
        Assert.Equal(expected, HexAiEditGlue.Walk(at, key, count));

    [Fact]
    public void A_problem_is_one_line_with_its_line_number() =>
        Assert.Equal("line 12: CS0103 The name 'x' does not exist", HexAiEditGlue.ProblemLine(11, "CS0103\r\nThe name 'x' does not exist"));

    [Fact]
    public void Edits_are_made_in_order_and_a_bad_one_is_refused()
    {
        Assert.Equal("a1c2", HexAiEditGlue.Apply("abc", [new PredictionEdit(1, 2, "1"), new PredictionEdit(3, 3, "2")]));
        Assert.Null(HexAiEditGlue.Apply("abc", [new PredictionEdit(2, 3, "x"), new PredictionEdit(1, 2, "y")]));
        Assert.Null(HexAiEditGlue.Apply("abc", [new PredictionEdit(0, 9, "x")]));
    }

    [Theory]
    [InlineData("x\nA\nB\ny", 2, 6, "x\nA\nB\ny", 2)]
    [InlineData("x\nA\nB\ny", 2, 6, "new\nx\nA\nB\ny", 6)]
    [InlineData("x\nA\nB\ny", 2, 6, "x\nA\nB\ny\nmore", 2)]
    [InlineData("x\nA\nB\ny", 2, 6, "x\nA\nC\ny", -1)]
    [InlineData("x\nA\nB\ny", 2, 6, "", -1)]
    [InlineData("a\nA\nB\nb\nA\nB\n", 2, 6, "a\nz\nA\nB\nb\nA\nB\n", 4)]
    public void The_target_is_found_again_where_it_moved(string before, int start, int end, string after, int expected)
    {
        int? want = expected < 0 ? null : expected;
        Assert.Equal(want, HexAiEditGlue.Rebase(before, start, end, after));
    }

    [Fact]
    public void Edits_move_with_the_target() =>
        Assert.Equal([new PredictionEdit(14, 16, "x")], HexAiEditGlue.Shift([new PredictionEdit(4, 6, "x")], 10));

    [Theory]
    [InlineData("a\nb\nc\n", "a\nb\nc\n", 0, 0)]
    [InlineData("a\nb\nc\n", "a\nB\nc\n", 1, 1)]
    [InlineData("a\nb\n", "a\nb\nc\nd\n", 2, 0)]
    [InlineData("a\nb\nc", "a", 0, 2)]
    [InlineData("", "x\ny\n", 2, 0)]
    [InlineData("x\ny\n", "", 0, 2)]
    [InlineData("a\nb\nc\nd\n", "b\nd\ne\n", 1, 2)]
    public void Changed_lines_are_counted_by_line(string before, string after, int added, int removed) =>
        Assert.Equal((added, removed), HexAiEditGlue.Lines(before, after));

    [Theory]
    [InlineData(true, 0, 0, "Loading")]
    [InlineData(false, 0, 0, "Waiting")]
    [InlineData(true, 3, 0, "Thinking")]
    [InlineData(false, 3, 2, "Writing")]
    [InlineData(true, 0, 1, "Writing")]
    public void Progress_says_what_has_come(bool swaps, int thinking, int answer, string expected) =>
        Assert.Equal(expected, HexAiEditGlue.ProgressOf(swaps, thinking, answer).ToString());

    [Fact]
    public void Seconds_and_lines_are_written_plainly()
    {
        Assert.Equal("3.1", HexAiEditGlue.Seconds(3.14));
        Assert.Equal("0.0", HexAiEditGlue.Seconds(-1));
        Assert.Equal(0, HexAiEditGlue.LinesWritten(string.Empty));
        Assert.Equal(3, HexAiEditGlue.LinesWritten("a\nb\nc"));
    }

    [Fact]
    public void The_card_is_shown_only_over_its_own_visible_editor()
    {
        (float, float, float, float) code = (100, 50, 700, 500), window = (0, 0, 1000, 600);
        Assert.True(HexAiEditGlue.CardVisible(true, true, code, window));

        // Another tab or document on screen, or the editor (or something above it) hidden: the Agentic face, a collapsed pane
        Assert.False(HexAiEditGlue.CardVisible(false, true, code, window));
        Assert.False(HexAiEditGlue.CardVisible(true, false, code, window));

        // No size, or wholly outside the area it can be seen in
        Assert.False(HexAiEditGlue.CardVisible(true, true, (100, 50, 100, 500), window));
        Assert.False(HexAiEditGlue.CardVisible(true, true, (1200, 50, 1700, 500), window));
        Assert.False(HexAiEditGlue.CardVisible(true, true, code, (800, 0, 1000, 600)));

        // Partly in sight is still shown, and it comes back when the editor does
        Assert.True(HexAiEditGlue.CardVisible(true, true, code, (650, 0, 1000, 600)));
        Assert.True(HexAiEditGlue.CardVisible(true, true, code, window));
    }
}
