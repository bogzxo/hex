using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// How a linked device's chats are grouped in the chat lists: Running and Finished by the row's state, what waits on
/// the person, the counts a group's head says, which groups and sections are open by default, and the folds the person
/// chose kept as one line of the settings.
/// </summary>
public sealed class HexLinkChatGroupsTests
{
    private static HexLinkRow Row(int id, string state, bool waiting = false, int? parent = null) =>
        new(id, $"chat {id}", "claude", "opus", parent, parent is null ? 0 : 1, state, string.Empty, 12, 0.5, 1, waiting, null);

    [Theory]
    [InlineData("running", "Running")]
    [InlineData("running (preparing the worktree)", "Running")]
    [InlineData("new", "Finished")]
    [InlineData("done", "Finished")]
    [InlineData("stopped", "Finished")]
    [InlineData("failed", "Finished")]
    [InlineData("runningish", "Finished")]
    public void A_rows_state_says_its_section(string state, string section) =>
        Assert.Equal(section, HexLinkChatGroups.SectionOf(Row(1, state)).ToString());

    [Fact]
    public void A_finished_row_waiting_on_the_person_is_in_Running()
    {
        Assert.Equal(HexLinkChatSection.Running, HexLinkChatGroups.SectionOf(Row(1, "done", waiting: true)));
        HexLinkRow approval = Row(2, "done") with { Approvals = [new HexAgentApprovalStatus(3, "Run the tests", "dotnet test")] };
        Assert.Equal(HexLinkChatSection.Running, HexLinkChatGroups.SectionOf(approval));
        Assert.True(HexLinkChatGroups.NeedsYou(approval, null));
    }

    [Fact]
    public void A_failure_needs_the_person_only_while_unseen()
    {
        HexLinkRow failed = Row(1, "failed");
        Assert.False(HexLinkChatGroups.NeedsYou(failed, null));
        Assert.False(HexLinkChatGroups.NeedsYou(failed, _ => false));
        Assert.True(HexLinkChatGroups.NeedsYou(failed, _ => true));
        Assert.False(HexLinkChatGroups.NeedsYou(Row(2, "done"), _ => true));
    }

    [Fact]
    public void Counts_split_the_rows_and_count_what_needs_the_person()
    {
        HexLinkRow[] rows = [Row(1, "running"), Row(2, "running", waiting: true), Row(3, "done"), Row(4, "stopped"), Row(5, "failed"), Row(6, "done")];
        HexLinkChatCounts counts = HexLinkChatGroups.Count(rows, row => row.Id == 5);
        Assert.Equal(new HexLinkChatCounts(2, 4, 2), counts);
        Assert.Equal(6, counts.All);
        Assert.Equal([1, 2], HexLinkChatGroups.In(rows, HexLinkChatSection.Running).Select(row => row.Id));
        Assert.Equal([3, 4, 5, 6], HexLinkChatGroups.In(rows, HexLinkChatSection.Finished).Select(row => row.Id));
    }

    [Fact]
    public void Closing_finished_chats_leaves_families_with_something_running()
    {
        // 1 started 2 (finished) and 3 (running); 4 started 5, both finished; 6 is on its own; 7 waits on the person
        HexLinkRow[] rows = [Row(1, "done"), Row(2, "done", parent: 1), Row(3, "running", parent: 1), Row(4, "stopped"), Row(5, "failed", parent: 4), Row(6, "done"), Row(7, "done", waiting: true)];
        List<HexLinkRow> closable = HexLinkChatGroups.ClosableFinished(rows);
        Assert.Equal([2, 4, 5, 6], closable.Select(row => row.Id));
        Assert.Equal([2, 4, 6], HexLinkChatGroups.ClosableTops(closable).Select(row => row.Id));
    }

    [Fact]
    public void Counts_are_said_with_the_empty_parts_left_out()
    {
        const string running = "{count} running", finished = "{count} finished", none = "No agents";
        Assert.Equal("2 running · 5 finished", HexLinkChatGroups.CountsSaid(new(2, 5, 0), running, finished, none));
        Assert.Equal("3 finished", HexLinkChatGroups.CountsSaid(new(0, 3, 0), running, finished, none));
        Assert.Equal("1 running", HexLinkChatGroups.CountsSaid(new(1, 0, 1), running, finished, none));
        Assert.Equal("No agents", HexLinkChatGroups.CountsSaid(new(0, 0, 0), running, finished, none));
    }

    [Fact]
    public void A_group_is_open_by_default_while_something_runs_or_waits_there()
    {
        Assert.True(HexLinkChatGroups.DeviceUsuallyOpen(new(1, 9, 0)));
        Assert.True(HexLinkChatGroups.DeviceUsuallyOpen(new(0, 9, 1)));
        Assert.False(HexLinkChatGroups.DeviceUsuallyOpen(new(0, 9, 0)));
    }

    [Fact]
    public void Finished_is_folded_by_default_past_five()
    {
        Assert.True(HexLinkChatGroups.SectionUsuallyOpen(HexLinkChatSection.Running, 300));
        Assert.True(HexLinkChatGroups.SectionUsuallyOpen(HexLinkChatSection.Finished, 5));
        Assert.False(HexLinkChatGroups.SectionUsuallyOpen(HexLinkChatSection.Finished, 6));
    }

    [Fact]
    public void Folds_go_by_the_default_until_chosen_and_round_trip_through_their_line()
    {
        var folds = new HexChatListFolds();
        string device = new('A', 64);
        string finished = HexLinkChatGroups.SectionKey(device, HexLinkChatSection.Finished);
        Assert.True(folds.Open(HexLinkChatGroups.DeviceKey(device), usual: true));
        folds.Set(HexLinkChatGroups.DeviceKey(device), false);
        folds.Set(finished, true);
        Assert.False(folds.Open(device, usual: true));
        Assert.True(folds.Open(finished, usual: false));

        var again = new HexChatListFolds(folds.ToString());
        Assert.Equal(2, again.Count);
        Assert.False(again.Open(device, usual: true));
        Assert.True(again.Open(finished, usual: false));
        Assert.Equal(device + "/finished", finished);
        Assert.Equal(device + "/running", HexLinkChatGroups.SectionKey(device, HexLinkChatSection.Running));

        again.Forget(device);
        Assert.Null(again.Chosen(device));
    }

    [Fact]
    public void Folds_pass_over_what_they_cant_read_and_keep_the_newest_choices()
    {
        var folds = new HexChatListFolds("a=1;;b=2;=1;c=;d=0;a=0; e = 1 ");
        Assert.False(folds.Chosen("a"));
        Assert.Null(folds.Chosen("b"));
        Assert.False(folds.Chosen("d"));
        Assert.True(folds.Chosen("e"));
        Assert.Equal("d=0;a=0;e=1", folds.ToString());

        folds.Set("x;y=z", true);
        Assert.True(folds.Chosen("xyz"));

        var many = new HexChatListFolds();
        for (int i = 0; i < HexChatListFolds.MOST + 10; i++)
            many.Set($"k{i}", true);
        Assert.Equal(HexChatListFolds.MOST, many.Count);
        Assert.Null(many.Chosen("k0"));
        Assert.True(many.Chosen($"k{HexChatListFolds.MOST + 9}"));
    }
}
