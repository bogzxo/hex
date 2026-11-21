using Hex.Core;
using Hex.Dawn;

using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// The parts of edit prediction's glue that need no editor: a prediction's offsets as places in the document, where the
/// caret goes after it, the gates that keep a request back, and what the status bar's item says.
/// </summary>
public sealed class PredictionGlueTests
{
    private static readonly string[] lines = ["class A", "{", "    int x;", "}"];

    private static PredictionFacts Open => new(OnScreen: true, Code: true, Focused: true, ReadOnly: false, Selection: false,
        ExtraCarets: false, PopupOpen: false, On: true, LanguageOff: false, Allowed: true);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(7, 0, 7)]
    [InlineData(8, 1, 0)]
    [InlineData(10, 2, 0)]
    [InlineData(20, 2, 10)]
    [InlineData(21, 3, 0)]
    [InlineData(22, 3, 1)]
    [InlineData(99, 3, 1)]
    public void An_offset_is_a_place_in_the_lines(int offset, int line, int column) =>
        Assert.Equal(new UITextPosition(line, column), HexPredictionGlue.Position(lines, offset));

    [Fact]
    public void Edits_become_the_editors_in_order()
    {
        UITextEdit[]? edits = HexPredictionGlue.Edits([new PredictionEdit(19, 19, " = 1"), new PredictionEdit(21, 22, "}\n")], lines);

        Assert.NotNull(edits);
        Assert.Equal(new UITextEdit(new UITextPosition(2, 9), new UITextPosition(2, 9), " = 1"), edits[0]);
        Assert.Equal(new UITextEdit(new UITextPosition(3, 0), new UITextPosition(3, 1), "}\n"), edits[1]);
    }

    [Fact]
    public void Edits_past_the_end_or_overlapping_are_none()
    {
        Assert.Null(HexPredictionGlue.Edits([new PredictionEdit(0, 99, "x")], lines));
        Assert.Null(HexPredictionGlue.Edits([new PredictionEdit(4, 8, "x"), new PredictionEdit(6, 9, "y")], lines));
        Assert.Null(HexPredictionGlue.Edits([], lines));
    }

    [Fact]
    public void The_caret_after_is_found_inside_an_inserted_text()
    {
        // "    int x;" with " = 1" inserted after x, the caret after the 1: offset 23 in the text after
        PredictionEdit[] edits = [new PredictionEdit(19, 19, " = 1")];
        Assert.Equal(new UITextPosition(2, 13), HexPredictionGlue.CaretAfter(edits, lines, 23));
    }

    [Fact]
    public void The_caret_after_follows_lines_an_earlier_edit_added()
    {
        // A line added after the brace, the caret in "    int x;" after it is a line further down
        PredictionEdit[] edits = [new PredictionEdit(9, 9, "\n    // c")];
        Assert.Equal(new UITextPosition(3, 9), HexPredictionGlue.CaretAfter(edits, lines, 19 + 9));
    }

    [Fact]
    public void The_caret_after_on_the_line_an_edit_ended_on_moves_along_it()
    {
        // "int" made "long" on line 2, the caret after "x" moved one to the right
        PredictionEdit[] edits = [new PredictionEdit(14, 17, "long")];
        Assert.Equal(new UITextPosition(2, 10), HexPredictionGlue.CaretAfter(edits, lines, 20));
        Assert.Null(HexPredictionGlue.CaretAfter(edits, lines, null));
    }

    [Fact]
    public void Every_gate_open_asks()
    {
        Assert.Equal(PredictionGate.Open, HexPredictionGlue.Gate(Open));
    }

    [Fact]
    public void A_shut_gate_is_said()
    {
        Assert.Equal(PredictionGate.NotOnScreen, HexPredictionGlue.Gate(Open with { OnScreen = false }));
        Assert.Equal(PredictionGate.NotCode, HexPredictionGlue.Gate(Open with { Code = false }));
        Assert.Equal(PredictionGate.Off, HexPredictionGlue.Gate(Open with { On = false }));
        Assert.Equal(PredictionGate.LanguageOff, HexPredictionGlue.Gate(Open with { LanguageOff = true }));
        Assert.Equal(PredictionGate.Excluded, HexPredictionGlue.Gate(Open with { Allowed = false }));
        Assert.Equal(PredictionGate.ReadOnly, HexPredictionGlue.Gate(Open with { ReadOnly = true }));
        Assert.Equal(PredictionGate.NotFocused, HexPredictionGlue.Gate(Open with { Focused = false }));
        Assert.Equal(PredictionGate.Selection, HexPredictionGlue.Gate(Open with { Selection = true }));
        Assert.Equal(PredictionGate.Carets, HexPredictionGlue.Gate(Open with { ExtraCarets = true }));
        Assert.Equal(PredictionGate.PopupOpen, HexPredictionGlue.Gate(Open with { PopupOpen = true }));
    }

    [Fact]
    public void The_item_is_hidden_under_no_ai_and_off_code()
    {
        Assert.Equal(PredictionItemState.Hidden, HexPredictionGlue.ItemState(true, true, true, true, true, false, true, PredictionStatus.Ok, false));
        Assert.Equal(PredictionItemState.Hidden, HexPredictionGlue.ItemState(false, false, true, true, true, false, true, PredictionStatus.Ok, false));
    }

    [Fact]
    public void The_item_says_why_predictions_are_off_in_the_order_they_are_put_right()
    {
        Assert.Equal(PredictionItemState.Off, HexPredictionGlue.ItemState(false, true, false, false, false, true, false, PredictionStatus.Ok, false));
        Assert.Equal(PredictionItemState.NotSetUp, HexPredictionGlue.ItemState(false, true, true, false, false, true, false, PredictionStatus.Ok, false));
        Assert.Equal(PredictionItemState.FolderOff, HexPredictionGlue.ItemState(false, true, true, true, false, true, false, PredictionStatus.Ok, false));
        Assert.Equal(PredictionItemState.LanguageOff, HexPredictionGlue.ItemState(false, true, true, true, true, true, false, PredictionStatus.Ok, false));
        Assert.Equal(PredictionItemState.FileOff, HexPredictionGlue.ItemState(false, true, true, true, true, false, false, PredictionStatus.Ok, false));
    }

    [Fact]
    public void The_item_waits_only_after_a_moment_and_says_what_went_wrong()
    {
        Assert.Equal(PredictionItemState.On, HexPredictionGlue.ItemState(false, true, true, true, true, false, true, PredictionStatus.Waiting, false));
        Assert.Equal(PredictionItemState.Waiting, HexPredictionGlue.ItemState(false, true, true, true, true, false, true, PredictionStatus.Waiting, true));
        Assert.Equal(PredictionItemState.Failed, HexPredictionGlue.ItemState(false, true, true, true, true, false, true, PredictionStatus.Unreachable, false));
        Assert.Equal(PredictionItemState.On, HexPredictionGlue.ItemState(false, true, true, true, true, false, true, PredictionStatus.Off, false));
    }

    [Theory]
    [InlineData("On", PredictionStatus.Ok, "prediction.item_on")]
    [InlineData("Off", PredictionStatus.Ok, "prediction.item_off")]
    [InlineData("Waiting", PredictionStatus.Waiting, "prediction.item_waiting")]
    [InlineData("Failed", PredictionStatus.Unreachable, "prediction.item_unreachable")]
    [InlineData("Failed", PredictionStatus.Unauthorized, "prediction.item_unauthorized")]
    [InlineData("Failed", PredictionStatus.NoSuchModel, "prediction.item_no_model")]
    [InlineData("Failed", PredictionStatus.BadAnswer, "prediction.item_bad_answer")]
    public void The_items_words_are_by_its_state(string state, PredictionStatus status, string key) =>
        Assert.Equal(key, HexPredictionGlue.ItemKey(Enum.Parse<PredictionItemState>(state), status));

    [Fact]
    public void Every_word_of_the_item_is_in_the_localisation()
    {
        string strings = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Hex.Dawn", "Assets", "hex", "localisation", "en-gb.hor"));
        foreach (PredictionItemState state in Enum.GetValues<PredictionItemState>())
        {
            foreach (PredictionStatus status in Enum.GetValues<PredictionStatus>())
            {
                string key = HexPredictionGlue.ItemKey(state, status)["prediction.".Length..];
                Assert.Contains($"        {key}: \"", strings, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void An_excerpt_is_thirty_lines_round_the_caret_within_the_file()
    {
        string[] many = [.. Enumerable.Range(0, 100).Select(n => n.ToString())];
        string[] middle = HexPredictionGlue.Excerpt(many, 50);
        Assert.Equal(30, middle.Length);
        Assert.Equal("35", middle[0]);
        Assert.Equal("0", HexPredictionGlue.Excerpt(many, 2)[0]);
        Assert.Equal("99", HexPredictionGlue.Excerpt(many, 99)[^1]);
        Assert.Equal(4, HexPredictionGlue.Excerpt(lines, 1).Length);
    }
}
