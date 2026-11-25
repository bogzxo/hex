using Hex.Dawn;

namespace Hex.Tests;

/// <summary>"Add a comment above" with the caret on a body line goes above that line, not above the block's header.</summary>
public sealed class AiEditPlacementTests
{
    private const string TEXT = "import os\n\ndef total_price(items):\n    total = 0\n    for item in items:\n        total += item\n    return total\n";

    [Theory]
    [InlineData("add a comment above", true)]
    [InlineData("Insert a blank line below", true)]
    [InlineData("rename x to y", false)]
    [InlineData("make this faster", false)]
    public void Instructions_that_say_where_to_put_something_are_placements(string instruction, bool placement)
    {
        Assert.Equal(placement, HexAiEditGlue.IsPlacement(instruction));
    }

    [Fact]
    public void A_caret_on_a_body_line_targets_the_block_unless_it_is_a_placement()
    {
        int caret = TEXT.IndexOf("total = 0", StringComparison.Ordinal) + 2;

        (List<(int Start, int End)> block, _) = HexAiEditGlue.Targets(TEXT, [(caret, caret, caret)], 0);
        (List<(int Start, int End)> line, _) = HexAiEditGlue.Targets(TEXT, [(caret, caret, caret)], 0, lineOnly: true);

        Assert.Equal(TEXT.IndexOf("def total_price", StringComparison.Ordinal), block[0].Start);
        int start = TEXT.IndexOf("    total = 0", StringComparison.Ordinal);
        Assert.Equal((start, start + "    total = 0".Length), line[0]);
    }

    [Fact]
    public void A_selection_is_kept_as_it_is_for_a_placement()
    {
        int from = TEXT.IndexOf("total = 0", StringComparison.Ordinal), to = TEXT.IndexOf("return total", StringComparison.Ordinal);

        (List<(int Start, int End)> targets, _) = HexAiEditGlue.Targets(TEXT, [(from, to, to)], 0, lineOnly: true);

        Assert.True(targets[0].End > from + "total = 0".Length);
    }
}
