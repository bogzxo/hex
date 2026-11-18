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
    public void Of_two_same_hunks_the_one_where_it_was_seen_is_undone()
    {
        string before = "a\nx\nb\nc\nd\ne\nx\nf\n";
        string now = "a\ny\nb\nc\nd\ne\ny\nf\n";
        var hunks = HexHunkReview.Hunks(before, now);
        Assert.Equal(2, hunks.Count);
        Assert.Equal("a\ny\nb\nc\nd\ne\nx\nf\n", HexHunkReview.Undo(before, now, hunks[1].Old, hunks[1].New, hunks[1].Hunk.NewStart));
    }

    [Fact]
    public void A_file_is_written_back_in_its_own_encoding()
    {
        string path = Path.Combine(Path.GetTempPath(), "hex-hunk-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            File.WriteAllText(path, "one\ntwo\n", System.Text.Encoding.Unicode);
            var (text, encoding) = HexHunkReview.Read(path);
            HexHunkReview.Write(path, text.Replace("two", "three"), encoding);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal([0xFF, 0xFE], bytes[..2]);
            Assert.Equal("one\nthree\n", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_hunk_no_longer_there_is_left_alone()
    {
        string now = "one\ntwo\nthree\nfour\nfive\nsix\nseven\n";
        Assert.Null(HexHunkReview.Undo(BEFORE.Replace("\r\n", "\n"), now, ["two"], ["TWO"]));
    }
}
