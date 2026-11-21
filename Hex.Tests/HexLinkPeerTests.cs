using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// The other machine as this one sees it, over a link in memory: its fleet following a changing hub, and its chats
/// mirrored row by row as they stream, start and finish tools, grow past what a chat keeps, change their plan and
/// have rows taken out of the middle, the mirror always equal to the chat it mirrors, and its cards answering there.
/// </summary>
public sealed class HexLinkPeerTests
{
    // What a row shows, the same for a chat and its mirror
    private static string Shown(HexAgentEntry entry) =>
        $"{entry.Kind}|{entry.Text}|{entry.State}|{entry.Output}|{entry.ToolName}|{entry.Input}|{entry.Steps}|{entry.Progress}|{entry.Queued}|{HexAgentChat.IsPending(entry)}";

    // Whether a mirror shows what its chat does, read on each's own "thread"
    private static bool Same(LinkedPair pair, HexAgentChat source, HexAgentChat mirror)
    {
        pair.PeerA.Pump();
        string[] there, here;
        string headThere;
        lock (pair.A.Editor)
        {
            there = [.. source.Entries.Select(Shown)];
            headThere = $"{source.Title}|{source.Dropped}|{source.Running}|{source.Cost}|{source.Plan?.Title}|{source.Plan?.Items.Count}|{source.Plan?.Doing?.Text}|{source.Name}";
        }

        here = [.. mirror.Entries.Select(Shown)];
        string headHere = $"{mirror.Title}|{mirror.Dropped}|{mirror.Running}|{mirror.Cost}|{mirror.Plan?.Title}|{mirror.Plan?.Items.Count}|{mirror.Plan?.Doing?.Text}|{mirror.Name}";
        return there.SequenceEqual(here) && headThere == headHere;
    }

    private static void Moved(LinkedPair pair, HexAgentChat chat, Action change)
    {
        pair.A.OnEditor(change);
        pair.LinkA.ChatMoved(chat.Id);
        pair.LinkA.FleetMoved();
    }

    [Fact]
    public async Task The_fleet_mirror_follows_a_changing_hub()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        HexAgentChat first = new(1, "claude_code"), second = new(2, "codex") { Parent = 1, Depth = 1 };
        pair.A.OnEditor(() =>
        {
            pair.A.Hub.Add(first);
            first.Ask("Build the thing");
        });

        IReadOnlyList<HexLinkRow> fleet = await pair.PeerA.WatchFleet();
        HexLinkRow row = Assert.Single(fleet);
        Assert.Equal((1, "Build the thing", "claude_code", "running"), (row.Id, row.Name, row.Agent, row.State));

        pair.A.FleetExtras = new Dictionary<int, HexLinkChatExtra> { [2] = new(true, "Write the tests") };
        Moved(pair, first, () =>
        {
            first.Say("Done it.");
            first.Done();
            pair.A.Hub.Add(second);
            second.Ask("Write the tests");
            second.Rename("Tester");
        });

        Assert.True(Eventually(() => pair.PeerA.Fleet.Count == 2 && pair.PeerA.Fleet[0].State == "done"));
        Assert.Equal("Done it.", pair.PeerA.Fleet[0].Reply);
        HexLinkRow tester = pair.PeerA.Fleet[1];
        Assert.Equal((2, "Tester", 1, 1, true, "Write the tests"), (tester.Id, tester.Name, tester.Parent, tester.Depth, tester.Waiting, tester.PlanStep));

        Moved(pair, second, () => pair.A.Hub.Remove(first));
        Assert.True(Eventually(() => pair.PeerA.Fleet.Count == 1 && pair.PeerA.Fleet[0].Id == 2));
    }

    [Fact]
    public async Task The_fleet_is_sent_at_most_five_times_a_second_however_often_it_moves()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var chat = new HexAgentChat(1, "claude_code");
        pair.A.OnEditor(() => pair.A.Hub.Add(chat));
        await pair.PeerA.WatchFleet();

        int heard = 0;
        pair.PeerA.Changed = () => Interlocked.Increment(ref heard);
        long until = Environment.TickCount64 + 1000;
        int said = 0;
        while (Environment.TickCount64 < until)
        {
            Moved(pair, chat, () => chat.Say($"line {said++}"));
            Thread.Sleep(2);
        }

        Thread.Sleep(400);
        Assert.InRange(heard, 2, 8);
        Assert.True(said > 50);
    }

    [Fact]
    public async Task A_mirrored_chat_equals_its_chat_through_streaming_tools_new_rows_and_a_plan()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var chat = new HexAgentChat(5, "claude_code");
        HexAgentEntry? reply = null;
        pair.A.OnEditor(() =>
        {
            pair.A.Hub.Add(chat);
            chat.Ask("Explain the link");
            reply = chat.Say("It");
        });

        HexAgentChat mirror = await pair.PeerA.OpenChat(5);
        Assert.Equal(5, mirror.Id);
        Assert.Same(mirror, await pair.PeerA.OpenChat(5));
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Assert.True(mirror.Running);

        // Text streaming into the same row
        for (int i = 0; i < 20; i++)
            Moved(pair, chat, () => { reply!.Text += $" word{i}"; chat.Touch(reply); });
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Assert.EndsWith("word19", mirror.Entries[1].Text);

        // A tool started, then done
        Moved(pair, chat, () => chat.Tool("t1", "hex_read", "{\"path\":\"a\"}"));
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Assert.Equal(HexAgentToolState.Running, mirror.Entries[2].State);
        Moved(pair, chat, () => chat.ToolDone("t1", true, "the file"));
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Assert.Equal(("the file", HexAgentToolState.Done), (mirror.Entries[2].Output, mirror.Entries[2].State));

        // A new row, a plan, a name and the turn done
        Moved(pair, chat, () =>
        {
            chat.Say("That's it.");
            chat.SetPlan(new HexAgentPlan("Link", [new HexPlanItem("Session", HexPlanState.Done), new HexPlanItem("Host", HexPlanState.Doing)]));
            chat.Rename("Explainer");
            chat.Cost = 0.25;
            chat.Done();
        });
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Assert.Equal("Host", mirror.Plan?.Doing?.Text);
        Assert.False(mirror.Running);
        Assert.Equal("Explainer", mirror.Shown);

        // Closed, it's no longer patched
        pair.PeerA.CloseChat(5);
        Assert.Empty(pair.PeerA.Chats);
    }

    [Fact]
    public async Task A_chat_past_what_it_keeps_drops_the_same_rows_in_its_mirror()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var chat = new HexAgentChat(7, "claude_code");
        pair.A.OnEditor(() =>
        {
            pair.A.Hub.Add(chat);
            chat.Ask("Go on and on");
        });

        HexAgentChat mirror = await pair.PeerA.OpenChat(7);
        for (int batch = 0; batch < 6; batch++)
        {
            Moved(pair, chat, () =>
            {
                for (int i = 0; i < 60; i++)
                    chat.Say($"row {batch}.{i}");
            });
        }

        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Assert.True(chat.Dropped > 0);
        Assert.Equal(chat.Dropped, mirror.Dropped);
        Assert.True(mirror.Entries.Count <= HexAgentChat.MOST_ENTRIES + HexAgentChat.SPILL_BATCH);

        // A row taken out of the middle: the chat sent whole again, still the same
        HexAgentEntry? queued = null;
        Moved(pair, chat, () => queued = chat.Steer("wait, one more thing"));
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
        Moved(pair, chat, () => chat.Withdraw(queued!));
        Assert.True(Eventually(() => Same(pair, chat, mirror)));
    }

    [Fact]
    public async Task A_mirrored_cards_answer_on_the_other_machine()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer);
        var chat = new HexAgentChat(9, "claude_code");
        pair.A.OnEditor(() =>
        {
            pair.A.Hub.Add(chat);
            chat.Ask("Deploy");
            chat.Approval("Run git push", "git push origin", _ => { });
            chat.Question("Which branch?", ["main", "development"], 1, 600, _ => { });
        });

        HexAgentChat mirror = await pair.PeerA.OpenChat(9);
        HexAgentEntry approval = mirror.FirstPending(HexAgentEntryKind.Approval)!;
        HexAgentEntry question = mirror.FirstPending(HexAgentEntryKind.Question)!;
        Assert.Equal(["main", "development"], question.Choices);
        Assert.Equal(1, question.Recommended);
        Assert.NotNull(question.Deadline);

        Assert.True(mirror.Decide(approval, true));
        Assert.True(mirror.Reply(question, "development"));
        Assert.True(Eventually(() => pair.A.LinkTools.Called.Any(call => call.Name == "hex_agent_approve")));
        var (_, arguments) = pair.A.LinkTools.Called.Single(call => call.Name == "hex_agent_approve");
        Assert.Equal((9, 1, true), (arguments["id"]!.GetValue<int>(), arguments["request"]!.GetValue<int>(), arguments["allow"]!.GetValue<bool>()));
        Assert.True(Eventually(() => pair.A.Answers.Contains((9, "development"))));
    }

    [Fact]
    public async Task A_chat_that_goes_is_said_gone_and_one_never_there_isnt_found()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var chat = new HexAgentChat(11, "claude_code");
        pair.A.OnEditor(() => pair.A.Hub.Add(chat));
        await pair.PeerA.OpenChat(11);
        Assert.Equal(HexLinkErrors.NOT_FOUND, await CodeOf(pair.PeerA.OpenChat(12)));

        Moved(pair, chat, () => pair.A.Hub.Remove(chat));
        Assert.True(Eventually(() =>
        {
            pair.PeerA.Pump();
            return pair.PeerA.IsGone(11);
        }));
    }

    [Fact]
    public async Task Chat_get_says_a_chat_is_unchanged_at_the_version_the_asker_has()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var chat = new HexAgentChat(3, "claude_code");
        pair.A.OnEditor(() =>
        {
            pair.A.Hub.Add(chat);
            chat.Ask("hi");
        });

        JsonObject whole = (JsonObject)(await pair.PeerA.Session.Ask("chat/get", new JsonObject { ["id"] = 3 }))!;
        Assert.True(whole["whole"]!.GetValue<bool>());
        int version = whole["version"]!.GetValue<int>();
        JsonObject again = (JsonObject)(await pair.PeerA.Session.Ask("chat/get", new JsonObject { ["id"] = 3, ["since"] = version }))!;
        Assert.True(again["unchanged"]!.GetValue<bool>());
    }

    [Fact]
    public void A_patch_with_a_gap_in_it_is_refused_and_changes_nothing()
    {
        var mirror = new HexAgentChat(1, "claude_code");
        Assert.True(mirror.MirrorRows(0, 2, [(0, new HexAgentEntry(HexAgentEntryKind.User, "a")), (1, new HexAgentEntry(HexAgentEntryKind.Text, "b"))], whole: true));
        Assert.False(mirror.MirrorRows(0, 4, [(3, new HexAgentEntry(HexAgentEntryKind.Text, "d"))]));
        Assert.False(mirror.MirrorRows(0, 2, [(5, new HexAgentEntry(HexAgentEntryKind.Text, "x"))]));
        Assert.Equal(["a", "b"], mirror.Entries.Select(entry => entry.Text));

        // One off the top and one more on the end
        Assert.True(mirror.MirrorRows(1, 2, [(1, new HexAgentEntry(HexAgentEntryKind.Text, "c"))]));
        Assert.Equal(["b", "c"], mirror.Entries.Select(entry => entry.Text));
        Assert.Equal(1, mirror.Dropped);
    }

    [Fact]
    public void A_hostile_row_is_read_with_limits_and_one_of_no_kind_refused()
    {
        var row = new JsonObject
        {
            ["kind"] = "Tool",
            ["text"] = new string('x', 1_000_000),
            ["state"] = "Exploded",
            ["seconds"] = double.MaxValue,
            ["steps"] = -5,
            ["choices"] = new JsonArray(1, 2),
            ["output"] = 12
        };
        HexAgentEntry entry = HexLinkChats.EntryOf(row, null, null)!;
        Assert.Equal(256 * 1024, entry.Text.Length);
        Assert.Equal(HexAgentToolState.Done, entry.State);
        Assert.Equal(0, entry.Steps);
        Assert.Null(entry.Output);
        Assert.Null(HexLinkChats.EntryOf(new JsonObject { ["kind"] = "Root" }, null, null));
        Assert.Null(HexLinkChats.EntryOf(new JsonObject { ["kind"] = "99" }, null, null));
    }
}
