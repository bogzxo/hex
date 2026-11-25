using Hex.Core;
using Hex.Dawn;

using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>
/// Next-edit suggestions without a model (docs/next-edit-plan.md, 2 and phase 2): renames noticed in the history, the
/// places left found outside strings and comments, the queue that holds them and the pure parts of showing them.
/// </summary>
public sealed class NextEditTests
{
    private const string PATH = "Foo.cs";

    private static string Join(params string[] lines) => string.Join('\n', lines);

    // A clock the tests move
    private sealed class TestClock : TimeProvider
    {
        private long ticks;

        public override long GetTimestamp() => ticks;

        public override long TimestampFrequency => 1000;

        public void Advance(TimeSpan by) => ticks += (long)by.TotalMilliseconds;
    }

    private static readonly string Before = Join("int count = 0;", "Use(count);", "return count;");
    private static readonly string After = Join("int total = 0;", "Use(count);", "return count;");

    /* Detection */

    [Fact]
    public void An_identifier_swap_is_a_rename()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Before, After);

        Rename rename = Assert.Single(tracker.Active(PATH));
        Assert.Equal("count", rename.OldName);
        Assert.Equal("total", rename.NewName);
        Assert.False(rename.Confirmed);
    }

    [Fact]
    public void A_name_that_only_appears_inside_other_words_is_not_a_rename()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Join("int count = 0;", "var account = 1;"), Join("int total = 0;", "var account = 1;"));

        Assert.Empty(tracker.Active(PATH));
    }

    [Fact]
    public void A_change_that_is_not_one_identifier_for_another_is_not_a_rename()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Join("int a = 0;", "Use(a);"), Join("int a = 1;", "Use(a);"));
        tracker.Observe(PATH, Join("int a = b;", "Use(a);"), Join("int c = d;", "Use(a);"));
        tracker.Observe(PATH, Join("Use(a);", "Use(a);"), Join("Use(a);", "Use(ab c);"));

        Assert.Empty(tracker.Active(PATH));
    }

    [Fact]
    public void Part_of_a_word_changed_is_the_whole_word_swapped()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Join("int count = 0;", "Use(count);"), Join("int counter = 0;", "Use(count);"));

        Rename rename = Assert.Single(tracker.Active(PATH));
        Assert.Equal(("count", "counter"), (rename.OldName, rename.NewName));
    }

    [Fact]
    public void Typing_the_old_name_back_cancels_it()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Before, After);
        Assert.Single(tracker.Active(PATH));

        tracker.Observe(PATH, After, Before);

        Assert.Empty(tracker.Active(PATH));
    }

    [Fact]
    public void A_new_name_that_is_gone_from_the_text_ends_it()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Before, After);

        tracker.Observe(PATH, After, Join("int x = 0;", "Use(count);", "return count;"));

        Assert.Empty(tracker.Active(PATH));
    }

    [Fact]
    public void Changing_a_second_occurrence_confirms_it()
    {
        var tracker = new RenameTracker();
        tracker.Observe(PATH, Before, After);
        string second = Join("int total = 0;", "Use(total);", "return count;");

        tracker.Observe(PATH, After, second);

        Assert.True(Assert.Single(tracker.Active(PATH)).Confirmed);
    }

    [Fact]
    public void A_rename_is_forgotten_after_five_minutes()
    {
        var clock = new TestClock();
        var tracker = new RenameTracker(clock);
        tracker.Observe(PATH, Before, After);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(tracker.Active(PATH));

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(tracker.Active(PATH));
    }

    [Fact]
    public void The_history_follows_a_name_typed_a_letter_at_a_time_and_typed_back()
    {
        var history = new PredictionHistory();
        history.Note(PATH, Before);
        string[] typed = ["int c = 0;", "int to = 0;", "int tota = 0;", "int total = 0;"];
        foreach (string line in typed)
            history.Note(PATH, Join(line, "Use(count);", "return count;"));

        Rename rename = Assert.Single(history.Renames.Active(PATH));
        Assert.Equal(("count", "total"), (rename.OldName, rename.NewName));
        Assert.Equal(2, history.Renames.Hint(PATH, After)!.Remaining);

        // Back to where it started within the one event
        history.Note(PATH, Join("int tota = 0;", "Use(count);", "return count;"));
        history.Note(PATH, Join("int count = 0;", "Use(count);", "return count;"));
        Assert.Empty(history.Renames.Active(PATH));
        Assert.Null(history.Renames.Hint(PATH, Before));
    }

    [Fact]
    public void A_rename_typed_over_a_selection_is_followed_once_the_file_was_seeded()
    {
        string[] file = ["def add(a, b):", "    return a + b", "", "print(add(2, 3))"];
        var history = new PredictionHistory();
        Assert.True(history.Seed("s.py", file));
        Assert.False(history.Seed("s.py", file));

        // The selection replaced by its first letter, then the rest typed on
        foreach (string name in new[] { "p", "pl", "plu", "plus" })
            history.Note("s.py", string.Join('\n', [$"def {name}(a, b):", file[1], file[2], file[3]]));

        Rename rename = Assert.Single(history.Renames.Active("s.py"));
        Assert.Equal(("add", "plus"), (rename.OldName, rename.NewName));
    }

    [Fact]
    public void Without_a_seed_the_first_edit_is_taken_as_the_start_and_the_old_name_is_lost()
    {
        string[] file = ["def add(a, b):", "    return a + b", "", "print(add(2, 3))"];
        var history = new PredictionHistory();
        foreach (string name in new[] { "p", "pl", "plus" })
            history.Note("s.py", string.Join('\n', [$"def {name}(a, b):", file[1], file[2], file[3]]));

        Assert.Empty(history.Renames.Active("s.py"));
    }

    [Fact]
    public void Closing_a_file_forgets_its_rename()
    {
        var history = new PredictionHistory();
        history.Note(PATH, Before);
        history.Note(PATH, After);
        Assert.Single(history.Renames.Active(PATH));

        history.Closed(PATH);

        Assert.Empty(history.Renames.Active(PATH));
    }

    /* Occurrences */

    private static void AssertPlaces(string text, string name, params int[] starts)
    {
        var rename = new Rename(PATH, name, "other", 0, false, 0);
        IReadOnlyList<(int Start, int End)> found = RenameTracker.Remaining(text, rename);
        Assert.Equal(starts, found.Select(place => place.Start).ToArray());
        Assert.All(found, place => Assert.Equal(name, text[place.Start..place.End]));
    }

    [Fact]
    public void Occurrences_in_C_sharp_skip_strings_and_comments()
    {
        string text = Join(
            "int total = 0; // count here is a comment",
            "var s = \"count\";",
            "var v = @\"a \"\"count\"\" b\";",
            "var c = 'c'; /* count */ count++;",
            "var account = count;",
            "/* multi",
            "   count */",
            "Use(count);");
        AssertPlaces(text, "count", text.IndexOf("count++", StringComparison.Ordinal), text.IndexOf("= count;", StringComparison.Ordinal) + 2,
            text.IndexOf("Use(count)", StringComparison.Ordinal) + 4);
    }

    [Fact]
    public void Occurrences_in_TypeScript_skip_template_strings_and_comments()
    {
        string text = Join(
            "const total = 0; // count",
            "const s = `a count b`;",
            "const t = 'count';",
            "/* count",
            "   count */ const u = count + 1;",
            "console.log(count);");
        AssertPlaces(text, "count", text.IndexOf("= count + 1", StringComparison.Ordinal) + 2, text.IndexOf("log(count)", StringComparison.Ordinal) + 4);
    }

    [Fact]
    public void Occurrences_in_Python_skip_hash_comments_and_docstrings()
    {
        string text = Join(
            "total = 0  # count",
            "\"\"\"count",
            "count again\"\"\"",
            "s = 'count'",
            "print(count)",
            "x = count + account");
        var rename = new Rename("foo.py", "count", "other", 0, false, 0);
        IReadOnlyList<(int Start, int End)> found = RenameTracker.Remaining(text, rename);

        Assert.Equal([text.IndexOf("print(count)", StringComparison.Ordinal) + 6, text.IndexOf("= count + ", StringComparison.Ordinal) + 2],
            found.Select(place => place.Start).ToArray());
    }

    [Fact]
    public void A_name_inside_another_word_or_after_a_digit_is_not_an_occurrence() =>
        AssertPlaces("account counter x_count 2count count", "count", 31);

    /* The queue */

    private static NextEdit Edit(int start, int end, NextEditSource source = NextEditSource.Rename, string replacement = "total") =>
        new(PATH, start, end, 1, [new PredictionEdit(start, end, replacement)], source, "rename", 0.9, new string('x', end - start));

    [Fact]
    public void Rebase_moves_what_follows_a_change_and_drops_what_it_touched()
    {
        var queue = new NextEditQueue();
        queue.Add(Edit(10, 15));
        queue.Add(Edit(30, 35));
        queue.Add(Edit(50, 55));

        // Three characters put in at 0, five replaced by two at 28..33 (overlapping the second)
        queue.Rebase([new NextEditChange(0, 0, 3), new NextEditChange(31, 5, 2)]);

        Assert.Equal([(13, 18), (50, 55)], queue.All.Select(edit => (edit.Start, edit.End)).ToArray());
        Assert.Equal((13, 18), (queue.All[0].Edits[0].Start, queue.All[0].Edits[0].End));
    }

    [Fact]
    public void Rebase_after_a_deletion_moves_back_and_a_change_at_the_edit_drops_it()
    {
        var queue = new NextEditQueue();
        queue.Add(Edit(20, 25));
        queue.Add(Edit(40, 45));

        queue.Rebase([new NextEditChange(0, 4, 0)]);
        Assert.Equal([16, 36], queue.All.Select(edit => edit.Start).ToArray());

        // A letter typed on to the end of the first
        queue.Rebase([new NextEditChange(21, 0, 1)]);
        Assert.Equal([36 + 1], queue.All.Select(edit => edit.Start).ToArray());
    }

    [Fact]
    public void Peek_is_the_nearest_after_the_caret_and_wraps_round_to_the_top()
    {
        var queue = new NextEditQueue();
        NextEdit a = Edit(10, 15), b = Edit(30, 35), c = Edit(50, 55);
        queue.Add(c);
        queue.Add(a);
        queue.Add(b);

        Assert.Equal([10, 30, 50], queue.All.Select(edit => edit.Start).ToArray());
        Assert.Same(a, queue.Peek(0));
        Assert.Same(b, queue.Peek(16));
        Assert.Same(b, queue.Peek(32));
        Assert.Same(c, queue.Peek(36));
        Assert.Same(a, queue.Peek(60));

        Assert.Same(c, queue.Next(30));
        Assert.Same(a, queue.Next(50));
        Assert.Same(b, queue.Previous(50));
        Assert.Same(c, queue.Previous(10));

        Assert.Same(c, queue.Take(36));
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void A_dismissed_suggestion_is_not_added_again_even_after_the_text_moved()
    {
        var queue = new NextEditQueue();
        NextEdit edit = Edit(10, 15);
        queue.Add(edit);
        queue.Dismiss(edit);
        Assert.Equal(0, queue.Count);

        Assert.False(queue.Add(Edit(10, 15)));
        queue.Rebase([new NextEditChange(0, 0, 2)]);
        Assert.False(queue.Add(Edit(12, 17)));
        Assert.True(queue.Add(Edit(40, 45)));
    }

    [Fact]
    public void The_queue_holds_at_most_thirty_two_and_drops_the_oldest()
    {
        var queue = new NextEditQueue();
        for (int i = 0; i < 40; i++)
            queue.Add(Edit(i * 10, i * 10 + 5));

        Assert.Equal(NextEditQueue.MOST, queue.Count);
        Assert.Equal(80, queue.All[0].Start);
    }

    [Fact]
    public void Drop_all_takes_out_one_source()
    {
        var queue = new NextEditQueue();
        queue.Add(Edit(0, 5));
        queue.Add(Edit(10, 15, NextEditSource.Model));

        queue.DropAll(NextEditSource.Rename);

        Assert.Equal(NextEditSource.Model, Assert.Single(queue.All).Source);
    }

    /* The glue */

    [Theory]
    [InlineData(5, 41, 0, "Tab ↓ 42")]
    [InlineData(50, 11, 0, "Tab ↑ 12")]
    [InlineData(56, 56, 0, "Tab")]
    [InlineData(5, 56, 3, "Tab ↓ 57 (3 more)")]
    public void The_hint_says_which_way_and_how_many_more(int caretLine, int targetLine, int more, string expected) =>
        Assert.Equal(expected, HexNextEditGlue.JumpHint(caretLine, targetLine, more));

    [Fact]
    public void A_change_between_two_texts_is_the_stretch_between_what_they_share()
    {
        Assert.Equal(new NextEditChange(4, 5, 5), HexNextEditGlue.Change("int count = 0;", "int total = 0;"));
        Assert.Equal(new NextEditChange(3, 0, 4), HexNextEditGlue.Change("abcdef", "abcXYZWdef"));
        Assert.Null(HexNextEditGlue.Change("same", "same"));
    }

    [Theory]
    [InlineData("a\nb\nc", "a\nb\nc")]
    [InlineData("a\nb\nc", "a\nbX\nc")]
    [InlineData("a\nb", "a\nb\nc")]
    [InlineData("a\nb\nc", "a\nb")]
    [InlineData("a\nb", "a\n\nb")]
    [InlineData("a\n\nb", "a\nb")]
    [InlineData("a\nb\nc", "a\nc")]
    [InlineData("x", "x\ny\nz")]
    [InlineData("a\nb\nb\nc", "a\nb\nc")]
    [InlineData("one\ntwo", "ONE\nTWO\nthree")]
    public void A_change_found_from_the_lines_is_the_one_found_from_the_text(string before, string after)
    {
        var was = new Horizon.UI.Components.UITextDocument { Text = before };
        string[] lines = [];
        int count = HexNextEditGlue.Snapshot(was, ref lines);
        var now = new Horizon.UI.Components.UITextDocument { Text = after };
        Assert.Equal(HexNextEditGlue.Change(before, after), HexNextEditGlue.Change(lines, count, now));
    }

    [Fact]
    public void A_suggestion_is_anchored_only_where_its_text_still_is()
    {
        NextEdit edit = HexNextEditGlue.ForRename(new Rename(PATH, "count", "total", 0, true, 0), After, 1, 0)[0];

        Assert.Equal((edit.Start, edit.End), HexNextEditGlue.Anchored(edit, After));
        Assert.Null(HexNextEditGlue.Anchored(edit, After.Replace("count", "cnt", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_rename_not_yet_confirmed_gives_only_the_place_nearest_the_caret()
    {
        var rename = new Rename(PATH, "count", "total", 0, false, 0);
        int second = After.IndexOf("return count", StringComparison.Ordinal) + 7;

        NextEdit near = Assert.Single(HexNextEditGlue.ForRename(rename, After, 1, second - 1));
        Assert.Equal(second, near.Start);

        // Past the last it comes round to the first
        string longer = After + "\n";
        NextEdit wrapped = Assert.Single(HexNextEditGlue.ForRename(rename, longer, 1, longer.Length));
        Assert.Equal(After.IndexOf("Use(count)", StringComparison.Ordinal) + 4, wrapped.Start);

        Assert.Equal(2, HexNextEditGlue.ForRename(rename with { Confirmed = true }, After, 1, 0).Count);
    }

    [Fact]
    public void Markers_are_on_the_line_and_columns_of_each_suggestion()
    {
        var rename = new Rename(PATH, "count", "total", 0, true, 0);
        List<NextEdit> edits = HexNextEditGlue.ForRename(rename, After, 1, 0);

        UIJumpMarker[] markers = HexNextEditGlue.Markers(edits, After.Split('\n'));

        Assert.Equal([new UIJumpMarker(1, 4, 9, "rename count → total"), new UIJumpMarker(2, 7, 12, "rename count → total")], markers);
    }

    [Fact]
    public void Accepting_all_is_one_undo_step()
    {
        var document = new UITextDocument { Text = After };
        List<NextEdit> edits = HexNextEditGlue.ForRename(new Rename(PATH, "count", "total", 0, true, 0), After, 1, 0);
        Assert.Equal(2, edits.Count);

        int taken = HexNextEditGlue.AcceptAll(document, edits);

        Assert.Equal(2, taken);
        Assert.Equal(Join("int total = 0;", "Use(total);", "return total;"), document.Text);
        document.Undo();
        Assert.Equal(After, document.Text);
    }

    [Fact]
    public void Accepting_all_skips_a_suggestion_whose_text_has_moved_on()
    {
        var document = new UITextDocument { Text = After };
        List<NextEdit> edits = HexNextEditGlue.ForRename(new Rename(PATH, "count", "total", 0, true, 0), After, 1, 0);
        document.Text = After.Replace("Use(count)", "Use(cnt  )", StringComparison.Ordinal);

        Assert.Equal(1, HexNextEditGlue.AcceptAll(document, edits));
        Assert.Equal(Join("int total = 0;", "Use(cnt  );", "return total;"), document.Text);
    }
}
