using Hex.Core;

namespace Hex.Tests;

/// <summary>The recent edits sent with a prompt: one event for typing in one place, six at most, a file's gone when it closes.</summary>
public sealed class PredictionHistoryTests
{
    private static string File(int changed, string with) =>
        string.Join('\n', Enumerable.Range(0, 100).Select(i => i == changed ? with : "line" + i)) + "\n";

    [Fact]
    public void A_change_is_one_hunk_with_three_lines_of_context()
    {
        var history = new PredictionHistory();
        history.Note("a.cs", File(-1, ""));
        history.Note("a.cs", File(10, "LINE10"));

        PredictionEvent change = Assert.Single(history.Events("a.cs"));
        Assert.Equal("@@ -8,7 +8,7 @@\n line7\n line8\n line9\n-line10\n+LINE10\n line11\n line12\n line13\n", change.Diff);
        Assert.False(change.Predicted);
    }

    [Fact]
    public void Typing_on_in_the_same_place_runs_into_one_event()
    {
        var history = new PredictionHistory();
        history.Note("a.cs", File(-1, ""));
        history.Note("a.cs", File(10, "L"));
        history.Note("a.cs", File(10, "LI"));
        history.Note("a.cs", File(10, "LIN"));

        PredictionEvent change = Assert.Single(history.Events("a.cs"));
        Assert.Contains("-line10\n+LIN\n", change.Diff);
    }

    [Fact]
    public void Changes_far_apart_are_events_of_their_own_and_six_are_kept()
    {
        var history = new PredictionHistory();
        string text = File(-1, "");
        history.Note("a.cs", text);
        for (int i = 0; i < 8; i++)
        {
            string[] lines = text.Split('\n');
            lines[i * 12] = "changed" + i;
            text = string.Join('\n', lines);
            history.Note("a.cs", text);
        }

        IReadOnlyList<PredictionEvent> events = history.Events("a.cs");
        Assert.Equal(6, events.Count);
        Assert.Contains("+changed2", events[0].Diff);
        Assert.Contains("+changed7", events[^1].Diff);
    }

    [Fact]
    public void A_files_own_events_come_last_and_go_when_it_closes()
    {
        var history = new PredictionHistory();
        history.Note("a.cs", "a\n");
        history.Note("a.cs", "A\n");
        history.Note("b.cs", "b\n");
        history.Note("b.cs", "B\n");

        Assert.Equal(["a.cs", "b.cs"], history.Events("b.cs").Select(change => change.Path));
        Assert.Equal(["b.cs", "a.cs"], history.Events("a.cs").Select(change => change.Path));

        history.Closed("a.cs");
        Assert.Equal(["b.cs"], history.Events("b.cs").Select(change => change.Path));
        history.Note("a.cs", "again\n");
        Assert.Single(history.Events("a.cs"));
    }

    [Fact]
    public void An_accepted_prediction_is_its_own_event_marked_as_one()
    {
        var history = new PredictionHistory();
        history.Note("a.cs", "int x;\n");
        history.Accepted("a.cs", "int x;\nint y;\n", "int x;\nint y = 2;\n");

        IReadOnlyList<PredictionEvent> events = history.Events("a.cs");
        Assert.Equal(2, events.Count);
        Assert.False(events[0].Predicted);
        Assert.True(events[1].Predicted);
        Assert.Contains("+int y = 2;", events[1].Diff);
    }

    [Fact]
    public void A_change_over_1500_bytes_isnt_kept()
    {
        var history = new PredictionHistory();
        history.Note("a.cs", "x\n");
        history.Note("a.cs", new string('y', 2000) + "\n");

        Assert.Empty(history.Events("a.cs"));
    }
}
