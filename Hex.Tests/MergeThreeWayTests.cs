using Hex.Core.Merge;

namespace Hex.Tests;

public sealed class MergeThreeWayTests
{
    [Fact]
    public void Edits_in_different_places_merge_clean_with_both_edits()
    {
        var regions = Merge("a\nb\nc\nd\ne", "A\nb\nc\nd\ne", "a\nb\nc\nd\nE");
        Assert.DoesNotContain(regions, region => region is MergeRegion.Conflict);
        Assert.Equal("A\nb\nc\nd\nE", MergeThreeWay.Marked(regions));
    }

    [Fact]
    public void Two_separate_overlaps_are_two_conflicts_each_resolved_on_its_own()
    {
        var regions = Merge("a\nb\nc\nd\ne", "a\nm1\nc\nd\nm2", "a\nt1\nc\nd\nt2");
        var conflicts = regions.OfType<MergeRegion.Conflict>().ToArray();
        Assert.Equal(2, conflicts.Length);
        conflicts[0].Resolution = new(MergeChoice.Mine);
        conflicts[1].Resolution = new(MergeChoice.Theirs);
        Assert.Equal("a\nm1\nc\nd\nt2", MergeThreeWay.Marked(regions));
    }

    [Fact]
    public void The_same_change_on_both_sides_is_clean()
    {
        var regions = Merge("a\nb\nc", "a\nx\nc", "a\nx\nc");
        Assert.DoesNotContain(regions, region => region is MergeRegion.Conflict);
        Assert.Equal("a\nx\nc", MergeThreeWay.Marked(regions));
    }

    [Fact]
    public void Lines_added_at_the_same_point_conflict()
    {
        var regions = Merge("a\nb", "a\nmine\nb", "a\ntheirs\nb");
        var conflict = Assert.Single(regions.OfType<MergeRegion.Conflict>());
        Assert.Equal(["mine"], conflict.MineLines);
        Assert.Equal(["theirs"], conflict.TheirsLines);
        Assert.Empty(conflict.BaseLines!);
    }

    [Fact]
    public void A_deletion_on_one_side_merges_with_an_untouched_other_side()
    {
        var regions = Merge("a\nb\nc", "a\nc", "a\nb\nc");
        Assert.DoesNotContain(regions, region => region is MergeRegion.Conflict);
        Assert.Equal("a\nc", MergeThreeWay.Marked(regions));
    }

    [Fact]
    public void A_change_applied_cleanly_remembers_the_base_lines_it_replaced()
    {
        var regions = Merge("a\nb\nc", "a\nB\nc", "a\nb\nc");
        var changed = Assert.Single(regions.OfType<MergeRegion.Clean>(), region => region.BaseLines is not null);
        Assert.Equal(["B"], changed.Lines);
        Assert.Equal(["b"], changed.BaseLines);
    }

    [Fact]
    public void Resolving_every_conflict_one_way_keeps_that_side_and_everything_that_merged_by_itself()
    {
        // Only line 2 is changed on both sides; theirs taking out 4, adding x and end, and mine adding a line after 5 all
        // merge by themselves whichever way the one conflict goes
        string[] basis = ["1", "2", "3", "4", "5", "6", "7", "8"];
        string[] mine = ["1", "m", "3", "4", "5", "mine only", "6", "7", "8"];
        string[] theirs = ["1", "t", "3", "5", "6", "x", "7", "8", "end"];
        string[] keptMine = ["1", "m", "3", "5", "mine only", "6", "x", "7", "8", "end"];
        string[] keptTheirs = ["1", "t", "3", "5", "mine only", "6", "x", "7", "8", "end"];
        Assert.Single(MergeThreeWay.Merge(basis, mine, theirs).OfType<MergeRegion.Conflict>());
        foreach (var (choice, expected) in new[] { (MergeChoice.Mine, keptMine), (MergeChoice.Theirs, keptTheirs) })
        {
            var regions = MergeThreeWay.Merge(basis, mine, theirs);
            foreach (MergeRegion.Conflict conflict in regions.OfType<MergeRegion.Conflict>())
                conflict.Resolution = new(choice);
            Assert.Equal(string.Join("\n", expected), MergeThreeWay.Marked(regions));
        }
    }

    [Fact]
    public void A_conflict_leaves_out_the_lines_both_sides_share_at_its_ends()
    {
        var regions = Merge("a\nb\nc", "a\nhead\nmine\ntail\nc", "a\nhead\ntheirs\ntail\nc");
        var conflict = Assert.Single(regions.OfType<MergeRegion.Conflict>());
        Assert.Equal(["mine"], conflict.MineLines);
        Assert.Equal(["theirs"], conflict.TheirsLines);
        Assert.Equal(["b"], conflict.BaseLines);
        Assert.Equal("a\nhead\n<<<<<<< Mine\nmine\n||||||| Base\nb\n=======\ntheirs\n>>>>>>> Theirs\ntail\nc", MergeThreeWay.Marked(regions));
    }

    private static IReadOnlyList<MergeRegion> Merge(string basis, string mine, string theirs) =>
        MergeThreeWay.Merge(basis.Split('\n'), mine.Split('\n'), theirs.Split('\n'));
}
