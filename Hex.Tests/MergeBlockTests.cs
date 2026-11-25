using System.Text;
using Hex.Core.Merge;

namespace Hex.Tests;

public sealed class MergeBlockTests
{
    private const string Marked = "start\n<<<<<<< Mine\nmine\n||||||| Base\nbase\n=======\ntheirs\n>>>>>>> Theirs\nend\n";

    private static (MergeModel Model, List<MergeBlock> Blocks) Open(string text = Marked)
    {
        var model = MergeModel.FromMarkers(text);
        return (model, MergeBlock.From(model));
    }

    [Fact]
    public void Existing_markers_become_regions_and_the_result_text_has_none()
    {
        var (model, blocks) = Open();
        Assert.Equal(3, blocks.Count);
        Assert.Equal(1, model.ConflictCount);
        Assert.True(blocks[1].IsConflict);
        Assert.False(blocks[1].Resolved);
        Assert.Equal("start\nbase\nend\n", MergeBlock.Join(blocks));
        Assert.DoesNotContain("<<<<<<<", MergeBlock.Join(blocks));
    }

    [Fact]
    public void A_conflict_with_no_base_is_empty_until_answered()
    {
        var (_, blocks) = Open("a\n<<<<<<< Mine\nx\n=======\ny\n>>>>>>> Theirs\nb\n");
        Assert.Equal("a\nb\n", MergeBlock.Join(blocks));
        Assert.False(blocks[1].Resolved);
    }

    [Theory]
    [InlineData((int)MergeChoice.Mine, "start\nmine\nend\n")]
    [InlineData((int)MergeChoice.Theirs, "start\ntheirs\nend\n")]
    [InlineData((int)MergeChoice.BothMineFirst, "start\nmine\ntheirs\nend\n")]
    [InlineData((int)MergeChoice.BothTheirsFirst, "start\ntheirs\nmine\nend\n")]
    public void Each_choice_replaces_the_region_text_and_resolves_it(int choice, string expected)
    {
        var (model, blocks) = Open();
        blocks[1] = blocks[1].Choose((MergeChoice)choice);
        Assert.True(blocks[1].Resolved);
        Assert.Equal(expected, MergeBlock.Join(blocks));
        Assert.Equal(expected, Encoding.UTF8.GetString(model.EncodeResult(MergeBlock.Join(blocks))));
    }

    [Fact]
    public void Undo_choice_takes_the_region_back_to_unresolved_base_text()
    {
        var (_, blocks) = Open();
        blocks[1] = blocks[1].Choose(MergeChoice.Theirs).Choose(MergeChoice.Unresolved);
        Assert.False(blocks[1].Resolved);
        Assert.Equal("start\nbase\nend\n", MergeBlock.Join(blocks));
    }

    [Fact]
    public void Typing_inside_an_unresolved_region_resolves_it_by_hand()
    {
        var (_, blocks) = Open();
        int selected = 0;
        // "start\n" is 6 characters, "base" follows: replace its first letter
        MergeBlock.ApplyEdit(blocks, 6, 7, "B", ref selected);
        Assert.True(blocks[1].Resolved);
        Assert.Equal(1, selected);
        Assert.Equal("start\nBase\nend\n", MergeBlock.Join(blocks));
    }

    [Fact]
    public void Typing_in_a_clean_region_leaves_conflicts_unresolved()
    {
        var (_, blocks) = Open();
        int selected = 0;
        MergeBlock.ApplyEdit(blocks, 0, 1, "S", ref selected);
        Assert.False(blocks[1].Resolved);
        Assert.Equal("Start\nbase\nend\n", MergeBlock.Join(blocks));
    }

    [Fact]
    public void Typing_at_an_empty_selected_region_goes_into_it()
    {
        var (_, blocks) = Open("a\n<<<<<<< Mine\nx\n=======\ny\n>>>>>>> Theirs\nb\n");
        int selected = 1;
        MergeBlock.ApplyEdit(blocks, 2, 2, "z\n", ref selected);
        Assert.True(blocks[1].Resolved);
        Assert.Equal("a\nz\nb\n", MergeBlock.Join(blocks));
    }

    [Fact]
    public void Edit_seeds_an_empty_region_with_both_sides()
    {
        var (_, blocks) = Open("a\n<<<<<<< Mine\nx\n=======\ny\n>>>>>>> Theirs\nb\n");
        blocks[1] = blocks[1].Seed();
        Assert.True(blocks[1].Resolved);
        Assert.Equal("a\nx\ny\nb\n", MergeBlock.Join(blocks));
    }

    [Fact]
    public void Saving_writes_no_markers_and_keeps_the_file_line_endings()
    {
        var (model, blocks) = Open("start\r\n<<<<<<< Mine\r\nmine\r\n=======\r\ntheirs\r\n>>>>>>> Theirs\r\nend\r\n");
        blocks[1] = blocks[1].Choose(MergeChoice.BothMineFirst);
        string saved = Encoding.UTF8.GetString(model.EncodeResult(MergeBlock.Join(blocks)));
        Assert.Equal("start\r\nmine\r\ntheirs\r\nend\r\n", saved);
        Assert.DoesNotContain("<<<<<<<", saved);
        Assert.DoesNotContain(">>>>>>>", saved);
    }
}
