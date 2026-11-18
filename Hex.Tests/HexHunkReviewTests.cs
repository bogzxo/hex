using Hex.Core.Git;

namespace Hex.Tests;

/// <summary>An agent's changes to a file as hunks, one put back as it was and the others kept.</summary>
public sealed class HexHunkReviewTests
{
    private const string BEFORE = "one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\n";

    [Fact]
    public void One_hunk_is_put_back_and_the_others_stay()
    {
        string now = "one\r\nTWO\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\neight\r\n";
        var hunks = HexHunkReview.Hunks(BEFORE, now);
        Assert.Equal(2, hunks.Count);

        // The change of the second line undone, the line put on at the end kept, its \r\n and last newline as they were
        string? undone = HexHunkReview.Undo(BEFORE, now, hunks[0].Old, hunks[0].New);
        Assert.Equal("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\neight\r\n", undone);

        // The one added at the end taken out
        Assert.Equal("one\r\nTWO\r\nthree\r\nfour\r\nfive\r\nsix\r\nseven\r\n", HexHunkReview.Undo(BEFORE, now, hunks[1].Old, hunks[1].New));
    }

    [Fact]
    public void A_hunk_no_longer_there_is_left_alone()
    {
        string now = "one\ntwo\nthree\nfour\nfive\nsix\nseven\n";
        Assert.Null(HexHunkReview.Undo(BEFORE.Replace("\r\n", "\n"), now, ["two"], ["TWO"]));
    }
}
