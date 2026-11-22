using System.Text;
using Hex.Core.Merge;

namespace Hex.Tests;

public sealed class MergeModelTests
{
    private const string Diff3 = "before\n<<<<<<< Mine\nmy line\n||||||| Base\nold line\n=======\ntheir line\n>>>>>>> Theirs\nafter\n";

    [Fact]
    public async Task No_change_preserves_the_text()
    {
        const string text = "one\ntwo\n";
        await Clean(text, text, text, text);
    }

    [Fact]
    public async Task Change_only_in_mine_is_applied() =>
        await Clean("old\n", "mine\n", "old\n", "mine\n");

    [Fact]
    public async Task Change_only_in_theirs_is_applied() =>
        await Clean("old\n", "old\n", "theirs\n", "theirs\n");

    [Fact]
    public async Task Same_change_is_applied_once() =>
        await Clean("old\n", "same\n", "same\n", "same\n");

    [Fact]
    public async Task Non_overlapping_edits_merge_cleanly() =>
        await Clean("a\nb\nc\nd\ne\n", "A\nb\nc\nd\ne\n", "a\nb\nc\nd\nE\n", "A\nb\nc\nd\nE\n");

    [Fact]
    public async Task Overlapping_edits_make_one_conflict()
    {
        MergeModel model = await Three("a\nb\nc\n", "a\nmine\nc\n", "a\ntheirs\nc\n");
        var conflict = Assert.Single(model.Conflicts);
        Assert.Equal(["b"], conflict.BaseLines);
        Assert.Equal(["mine"], conflict.MineLines);
        Assert.Equal(["theirs"], conflict.TheirsLines);
        Assert.Equal(1, model.UnresolvedCount);
    }

    [Fact]
    public async Task Different_insertions_at_the_same_line_conflict()
    {
        MergeModel model = await Three("a\nb\n", "a\nmine\nb\n", "a\ntheirs\nb\n");
        var conflict = Assert.Single(model.Conflicts);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(conflict.BaseLines));
        Assert.Equal(["mine"], conflict.MineLines);
        Assert.Equal(["theirs"], conflict.TheirsLines);
    }

    [Fact]
    public async Task Delete_versus_edit_conflicts()
    {
        MergeModel model = await Three("a\nb\nc\n", "a\nc\n", "a\nchanged\nc\n");
        var conflict = Assert.Single(model.Conflicts);
        Assert.Equal(["b"], conflict.BaseLines);
        Assert.Empty(conflict.MineLines);
        Assert.Equal(["changed"], conflict.TheirsLines);
    }

    [Fact]
    public async Task Different_line_endings_keep_mine() =>
        await Clean("a\nb\nc\nd\ne\n", "A\r\nb\r\nc\r\nd\r\ne\r\n",
            "a\nb\nc\nd\nE\n", "A\r\nb\r\nc\r\nd\r\nE\r\n");

    [Fact]
    public async Task Both_additions_have_an_empty_base()
    {
        MergeModel model = await Three("", "mine\n", "theirs\n");
        var conflict = Assert.Single(model.Conflicts);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(conflict.BaseLines));
        Assert.Equal(["mine"], conflict.MineLines);
        Assert.Equal(["theirs"], conflict.TheirsLines);
    }

    [Fact]
    public async Task Mine_without_a_final_newline_stays_that_way()
    {
        await Clean("old", "old", "new\n", "new");
        var model = await Three("old", "mine", "theirs\n");
        Assert.Single(model.Conflicts).Resolution = new(MergeChoice.Theirs);
        Assert.Equal("theirs", model.ToText());
    }

    [Fact]
    public void Two_way_markers_have_no_base()
    {
        const string text = "<<<<<<< HEAD\r\nmy line\r\n=======\r\ntheir line\r\n>>>>>>> branch";
        MergeModel model = MergeModel.FromMarkers(text);
        var conflict = Assert.Single(model.Conflicts);
        Assert.Null(conflict.BaseLines);
        Assert.Equal(["my line"], conflict.MineLines);
        Assert.Equal(["their line"], conflict.TheirsLines);
        Assert.Equal("\r\n", model.LineEnding);
        Assert.False(model.HasFinalNewline);
        Assert.Equal(text, model.ToText());
    }

    [Fact]
    public void Diff3_markers_keep_the_base() =>
        Assert.Equal(["old line"], Assert.Single(MergeModel.FromMarkers(Diff3).Conflicts).BaseLines);

    [Fact]
    public void Each_resolution_renders_the_expected_lines()
    {
        MergeModel model = MergeModel.FromMarkers(Diff3);
        var conflict = Assert.Single(model.Conflicts);
        Assert.Equal(Diff3, model.ToText());

        conflict.Resolution = new(MergeChoice.Mine);
        Assert.Equal("before\nmy line\nafter\n", model.ToText());
        conflict.Resolution = new(MergeChoice.Theirs);
        Assert.Equal("before\ntheir line\nafter\n", model.ToText());
        conflict.Resolution = new(MergeChoice.BothMineFirst);
        Assert.Equal("before\nmy line\ntheir line\nafter\n", model.ToText());
        conflict.Resolution = new(MergeChoice.BothTheirsFirst);
        Assert.Equal("before\ntheir line\nmy line\nafter\n", model.ToText());
        conflict.Resolution = new(MergeChoice.Custom, ["my choice", "second line"]);
        Assert.Equal("before\nmy choice\nsecond line\nafter\n", model.ToText());
        Assert.Equal(0, model.UnresolvedCount);

        conflict.Resolution = new(MergeChoice.Unresolved);
        Assert.Equal(1, model.UnresolvedCount);
        Assert.Equal(Diff3, model.ToText());
    }

    [Fact]
    public async Task Unresolved_result_round_trips_through_marker_model()
    {
        MergeModel model = await Three("a\nb\nc\n", "a\nmine\nc\n", "a\ntheirs\nc\n");
        MergeModel reopened = MergeModel.FromMarkers(model.ToText());
        Assert.Equal(model.ToText(), reopened.ToText());
        Assert.Equal(1, reopened.UnresolvedCount);
        Assert.Equal(model.Conflicts[0].BaseLines, reopened.Conflicts[0].BaseLines);
        Assert.Equal(model.Conflicts[0].MineLines, reopened.Conflicts[0].MineLines);
        Assert.Equal(model.Conflicts[0].TheirsLines, reopened.Conflicts[0].TheirsLines);
    }

    [Fact]
    public async Task Binary_input_keeps_exact_side_bytes()
    {
        byte[] mine = [1, 0, 2];
        byte[] theirs = [1, 0, 3];
        MergeModel model = await MergeModel.FromThree([1, 0], mine, theirs);
        Assert.True(model.IsBinary);
        var sides = Assert.IsType<MergeBinarySides>(model.BinarySides);
        Assert.Equal(mine, sides.Mine);
        Assert.Equal(theirs, sides.Theirs);
    }

    [Fact]
    public async Task Result_preserves_mines_encoding()
    {
        byte[] mine = [0xff, 0xfe, .. Encoding.Unicode.GetBytes("old\r\n")];
        MergeModel model = await MergeModel.FromThree(Bytes("old\n"), mine, Bytes("new\n"));
        byte[] expected = [0xff, 0xfe, .. Encoding.Unicode.GetBytes("new\r\n")];
        Assert.Equal(expected, model.ToBytes());
    }

    [Fact]
    public void Ordinary_text_is_a_single_clean_region()
    {
        const string text = "one\n=======\ntwo";
        MergeModel model = MergeModel.FromMarkers(text);
        Assert.IsType<MergeRegion.Clean>(Assert.Single(model.Regions));
        Assert.Equal(text, model.ToText());
    }

    private static async Task Clean(string basis, string mine, string theirs, string expected)
    {
        MergeModel model = await Three(basis, mine, theirs);
        Assert.Empty(model.Conflicts);
        Assert.Equal(expected, model.ToText());
    }

    private static async Task<MergeModel> Three(string basis, string mine, string theirs)
    {
        MergeModel model = await MergeModel.FromThree(Bytes(basis), Bytes(mine), Bytes(theirs));
        Assert.False(model.IsBinary);
        return model;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
