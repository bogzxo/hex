using Hex.Core;

namespace Hex.Tests;

public sealed class HexAgentReviewsTests
{
    private const string BEFORE = "one\ntwo\nthree\nfour\nfive\n";
    private const string AFTER = "one\n2\nthree\nfour\nfive\nsix\n";

    [Fact]
    public void ABaselineIsKeptOnceWhicheverChatsAndFilesHaveIt()
    {
        string big = string.Concat(Enumerable.Repeat("a line of code that repeats\n", 20_000));
        var reviews = new HexAgentReviews();
        reviews.Record(1, "/a/one.cs", big);
        reviews.Record(2, "/a/one.cs", big);
        reviews.Record(2, "/a/two.cs", big);
        Assert.Equal(1, reviews.DistinctTexts);
        Assert.Equal(big, reviews.Baseline(2, "/a/two.cs"));

        reviews.Forget(1);
        reviews.Drop(2, "/a/one.cs");
        Assert.Equal(1, reviews.DistinctTexts);
        reviews.SetBaseline(2, "/a/two.cs", "other");
        Assert.Equal(1, reviews.DistinctTexts);
        Assert.Equal("other", reviews.Baseline(2, "/a/two.cs"));
        reviews.Drop(2, "/a/two.cs");
        Assert.Equal(0, reviews.DistinctTexts);
    }

    [Fact]
    public void WhatDiffersIsRememberedForTheSameTexts()
    {
        var reviews = new HexAgentReviews();
        reviews.Record(1, "/a/file.cs", BEFORE);
        int reads = 0;
        Func<string, string?> current = _ =>
        {
            reads++;
            return AFTER;
        };
        var first = reviews.Differing(1, current);
        Assert.Equal([("/a/file.cs", 2, 1)], first);
        Assert.Equal(first, reviews.Differing(1, current));
        Assert.Equal((1, 2, 1), reviews.Totals(1, current));
        Assert.Empty(reviews.Differing(1, _ => BEFORE));
    }

    [Fact]
    public void FirstTouchIsTheBaseline()
    {
        var reviews = new HexAgentReviews();
        Assert.True(reviews.Record(1, "/a/file.cs", BEFORE));
        Assert.False(reviews.Record(1, "/a/file.cs", AFTER));
        Assert.Equal(BEFORE, reviews.Baseline(1, "/a/file.cs"));
        Assert.Null(reviews.Baseline(2, "/a/file.cs"));
    }

    [Fact]
    public void HunksAndTotalsCountTheChange()
    {
        var reviews = new HexAgentReviews();
        reviews.Record(1, "/a/file.cs", BEFORE);
        Assert.Equal(2, reviews.Hunks(1, "/a/file.cs", AFTER).Count);
        Assert.Equal((1, 2, 1), reviews.Totals(1, _ => AFTER));
        Assert.Equal((0, 0, 0), reviews.Totals(1, _ => BEFORE));
    }

    [Fact]
    public void RejectingAHunkPutsItsOriginalTextBack()
    {
        var hunks = HexAgentReviews.HunksOf(BEFORE, AFTER);
        Assert.Equal("one\ntwo\nthree\nfour\nfive\nsix\n", HexAgentReviews.Reject(BEFORE, AFTER, hunks[0]));
        Assert.Equal("one\n2\nthree\nfour\nfive\n", HexAgentReviews.Reject(BEFORE, AFTER, hunks[1]));
    }

    [Fact]
    public void RejectingEveryHunkFromTheLastGivesTheBaseline()
    {
        string text = AFTER;
        var hunks = HexAgentReviews.HunksOf(BEFORE, AFTER);
        for (int i = hunks.Count - 1; i >= 0; i--)
            text = HexAgentReviews.Reject(BEFORE, text, hunks[i]);
        Assert.Equal(BEFORE, text);
    }

    [Fact]
    public void AcceptingAHunkMovesTheBaselineOverIt()
    {
        var reviews = new HexAgentReviews();
        reviews.Record(1, "/a/file.cs", BEFORE);
        var hunks = reviews.Hunks(1, "/a/file.cs", AFTER);
        string moved = HexAgentReviews.Accept(BEFORE, AFTER, hunks[0]);
        Assert.Equal("one\n2\nthree\nfour\nfive\n", moved);
        reviews.SetBaseline(1, "/a/file.cs", moved);
        var left = reviews.Hunks(1, "/a/file.cs", AFTER);
        Assert.Single(left);
        Assert.Equal(1, left[0].NewCount);
    }

    [Fact]
    public void LineEndingsOfTheCurrentTextStay()
    {
        string before = "a\r\nb\r\nc\r\n", after = "a\r\nB\r\nc\r\n";
        var hunk = HexAgentReviews.HunksOf(before, after).Single();
        Assert.Equal(before, HexAgentReviews.Reject(before, after, hunk));
    }

    [Fact]
    public void ANewFileIsABaselineOfNothing()
    {
        var reviews = new HexAgentReviews();
        reviews.Record(1, "/a/new.cs", null);
        Assert.Equal((1, 2, 0), reviews.Totals(1, _ => "x\ny\n"));
        Assert.Equal(string.Empty, HexAgentReviews.Reject(string.Empty, "x\ny\n", reviews.Hunks(1, "/a/new.cs", "x\ny\n")[0]));
    }

    [Fact]
    public void BaselinesSurviveARestartAndGoWithTheirChat()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-reviews-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reviews = new HexAgentReviews();
            reviews.Record(7, "/a/file.cs", BEFORE);
            reviews.Record(7, "/a/other.cs", "x\n");
            reviews.Record(8, "/a/file.cs", "y\n");
            reviews.Keep(folder);
            Assert.True(File.Exists(Path.Combine(folder, "reviews", "review-7.json")));

            var started = new HexAgentReviews();
            started.Load(folder, [7, 9]);
            Assert.Equal(BEFORE, started.Baseline(7, "/a/file.cs"));
            Assert.Equal(2, started.Paths(7).Count);
            Assert.Null(started.Baseline(8, "/a/file.cs"));

            reviews.Forget(7);
            reviews.Keep(folder);
            Assert.False(File.Exists(Path.Combine(folder, "reviews", "review-7.json")));
            Assert.True(File.Exists(Path.Combine(folder, "reviews", "review-8.json")));

            reviews.Unload();
            Assert.Empty(reviews.Paths(8));
            reviews.Keep(folder);
            Assert.True(File.Exists(Path.Combine(folder, "reviews", "review-8.json")));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CodexFileChangesAndAntigravityTargetsNameTheirFiles()
    {
        string file = Path.Combine(Path.GetTempPath(), "x.cs");
        Assert.Equal([file], HexAgentReviews.PathsOf("file_change", "update " + file + "\n"));
        Assert.Equal(2, HexAgentReviews.PathsOf("file_change", "update " + file + "\nadd " + file + "2").Count);
        Assert.Equal(["/q/z.cs"], HexAgentReviews.PathsOf("replace_file_content", """{"TargetFile":"/q/z.cs","x":1}"""));
        Assert.Empty(HexAgentReviews.PathsOf("command", "ls"));
    }

    [Fact]
    public void ACallToldAsSoonAsItWasSeenTakesItsBaselineFromGit()
    {
        string file = Path.Combine(Path.GetTempPath(), "hex-late-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "after");
        try
        {
            var reviews = new HexAgentReviews { LateBaseline = _ => "committed" };
            string input = new System.Text.Json.Nodes.JsonObject { ["TargetFile"] = file }.ToJsonString();
            long now = Environment.TickCount64;
            var told = new HexAgentEntry(HexAgentEntryKind.Tool, "write_to_file") { ToolName = "write_to_file", Input = input, Started = now };
            reviews.Observe(5, told);
            Assert.Equal("after", reviews.Baseline(5, file));
            told.Ended = now;
            reviews.Told(5, told);
            Assert.Equal("committed", reviews.Baseline(5, file));

            // One told long after it was seen ran in between, the baseline taken at the start stands
            string other = file + "2";
            File.WriteAllText(other, "before");
            var slow = new HexAgentEntry(HexAgentEntryKind.Tool, "write_to_file")
            {
                ToolName = "write_to_file", Input = new System.Text.Json.Nodes.JsonObject { ["TargetFile"] = other }.ToJsonString(), Started = now
            };
            reviews.Observe(5, slow);
            slow.Ended = now + 500;
            reviews.Told(5, slow);
            Assert.Equal("before", reviews.Baseline(5, other));
            File.Delete(other);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ObserveKeepsTheFileOfAnEditCallOnly()
    {
        string file = Path.Combine(Path.GetTempPath(), "hex-review-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "before");
        try
        {
            var reviews = new HexAgentReviews();
            string input = new System.Text.Json.Nodes.JsonObject { ["file_path"] = file }.ToJsonString();
            reviews.Observe(3, "Read", input);
            Assert.Empty(reviews.Paths(3));
            reviews.Observe(3, "Edit", input);
            Assert.Equal("before", reviews.Baseline(3, file));
            reviews.Observe(3, "hex_editor", input);
            Assert.Single(reviews.Paths(3));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
