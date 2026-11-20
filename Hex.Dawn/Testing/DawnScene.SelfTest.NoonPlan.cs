using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Agentic's Plan tab with a long session's plan, dozens of steps done, one going, a few blocked and dropped, and the
// steps still to come. HEX_SELFTEST_SHOTS names a folder for pictures of it.
internal sealed partial class DawnScene
{
    // A plan the way a long session leaves it, most of it done
    private static HexAgentPlan LongPlan()
    {
        List<HexPlanItem> items = [];
        for (int i = 1; i <= 40; i++)
            items.Add(new HexPlanItem($"Finished step {i}: tidy the {(i % 3 == 0 ? "fleet" : i % 3 == 1 ? "composer" : "details")} code", HexPlanState.Done, $"d{i}",
                i % 4 == 0 ? $"merged as commit {i:x4}a" : null));
        items.Insert(12, new HexPlanItem("Shared tools: hex_type_hierarchy over the language server", HexPlanState.Dropped, "x1", "folded into the tooling work"));
        items.Insert(30, new HexPlanItem("Old menu study", HexPlanState.Dropped, "x2"));
        items.Add(new HexPlanItem("Rebuild the Plan tab as a plan manager", HexPlanState.Doing, "p1", "worktree hexwt-plan", 43,
            ["Hex.Dawn/Noon/DawnScene.Noon.Stage.cs", "Hex.Core/Agents/HexAgentPlan.cs"]));
        items.Add(new HexPlanItem("Engine: bigger glyph atlas for 200% scale", HexPlanState.Blocked, "b1", "waiting on the engine branch to merge"));
        items.Add(new HexPlanItem("Ship the Mac build", HexPlanState.Blocked, "b2", "the MacBook is offline"));
        items.Add(new HexPlanItem("Review the menus revamp with the person", HexPlanState.Pending, "n1"));
        items.Add(new HexPlanItem("Measure Agentic's frame time with 12 chats", HexPlanState.Pending, "n2"));
        items.Add(new HexPlanItem("Write the release notes", HexPlanState.Pending, "n3"));
        return new HexAgentPlan("Dawn revamp and text crispness", items);
    }

    private void TestNoonPlan(HexPointerScript test)
    {
        HexAgentChat? chat = null;

        void Shot(string name)
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name + ".png"));
        }

        test.Run(() =>
        {
            ShowFace(HexFace.Noon);
            chat = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Planner" };
            chat.Ask("carry on with the plan");
            chat.Say("Going on with the Plan tab.");
            chat.Done();
            chat.SetPlan(LongPlan());
            agentChats.Add(chat);
            OpenAgentChat(chat);
            ShowStage(NoonStage.Plan);
            noonWait = 0;
            noonStageDrawn = NOT_DRAWN;
        });
        test.Settle(0.6f);
        Label[] Labels() => [.. Descendants(noonPlanRows).OfType<Label>().Where(label => label.Visible)];
        int Shown(string text) => Labels().Count(label => label.Text.Contains(text, StringComparison.Ordinal));
        int Top(string text) => Array.FindIndex(Labels(), label => label.Text.Contains(text, StringComparison.Ordinal));
        Button? Labelled(string text) => Descendants(noonPlanRows).OfType<Button>().FirstOrDefault(button => button.Visible && button.Label.Contains(text, StringComparison.Ordinal));
        void Redrawn() => test.Run(() =>
        {
            RedrawPlan();
            noonWait = 0;
        });

        test.Check("noon plan: the step going on leads, then the blocked ones with their reasons, then what's next", () => noonStage == NoonStage.Plan
            && Shown("Rebuild the Plan tab") == 1 && Shown("the MacBook is offline") == 1 && Shown("Write the release notes") == 1
            && Top("Rebuild the Plan tab") < Top("Ship the Mac build") && Top("Ship the Mac build") < Top("Write the release notes"));
        test.Check("noon plan: the 40 steps done fold into one row, none of them listed and the dropped ones hidden", () =>
            Shown("Finished step") == 0 && Shown("Old menu study") == 0 && Labelled(Said("noon.plan_done_fold", ("count", "40"))) is not null);
        test.Check("noon plan: the head says how far it got and counts what's doing, blocked, to do and dropped", () =>
            Shown(Said("noon.plan_progress", ("done", "40"), ("all", "48"))) == 1
            && Descendants(noonPlanRows).OfType<Badge>().Select(badge => badge.Text).ToHashSet().SetEquals([
                Said("noon.plan_count_doing", ("count", "1")), Said("noon.plan_count_blocked", ("count", "2")),
                Said("noon.plan_count_pending", ("count", "3")), Said("noon.plan_count_dropped", ("count", "2")), "#43"]));
        test.Check("noon plan: the tab draws far fewer rows than the plan has steps", () => Descendants(noonPlanRows).Count() < 140);
        test.Run(() => Shot("noon-plan-long"));
        test.Settle(0.4f);

        // The steps done opened, the newest first
        test.Run(() => Labelled(Said("noon.plan_done_fold", ("count", "40")))!.OnPressed!());
        Redrawn();
        test.Settle(0.5f);
        test.Check("noon plan: opening the steps done lists them, the latest in the plan first", () =>
            Shown("Finished step") == 40 && Top("Finished step 40:") < Top("Finished step 1:"));
        test.Run(() =>
        {
            noonPlanDoneShown = false;
            noonPlanFilter = NoonPlanFilter.Blocked;
            RedrawPlan();
        });
        test.Settle(0.5f);
        test.Check("noon plan: Blocked lists only the blocked steps", () =>
            Shown("Ship the Mac build") == 1 && Shown("Engine: bigger glyph atlas") == 1 && Shown("Rebuild the Plan tab") == 0 && Shown("Write the release notes") == 0);
        test.Run(() => Shot("noon-plan-blocked"));
        test.Settle(0.4f);
        test.Run(() =>
        {
            noonPlanFilter = NoonPlanFilter.Done;
            RedrawPlan();
        });
        test.Settle(0.5f);
        test.Check("noon plan: Done lists the steps done and, after them, the dropped ones", () =>
            Shown("Finished step") == 40 && Shown("Old menu study") == 1 && Top("Finished step 1:") < Top("Old menu study"));

        // A step's details: its note, its files, its next move first and dropping it last in the error's colour
        test.Run(() =>
        {
            noonPlanFilter = NoonPlanFilter.Open;
            noonPlanOpened = "p1";
            RedrawPlan();
        });
        test.Settle(0.5f);
        test.Check("noon plan: a step's details have its files to open and Mark done first, Drop step last and in the error's colour", () =>
        {
            Button[] buttons = [.. Descendants(noonPlanRows).OfType<Button>().Where(button => button.Visible)];
            int done = Array.FindIndex(buttons, button => button.Label == Said("noon.plan_mark_done"));
            int drop = Array.FindIndex(buttons, button => button.Label == Said("noon.plan_drop"));
            return buttons.Any(button => button.Label.Contains("DawnScene.Noon.Stage.cs", StringComparison.Ordinal))
                && done >= 0 && buttons[done].Primary && drop > done && buttons[drop].Danger;
        });
        test.Run(() => Shot("noon-plan-details"));
        test.Settle(0.4f);

        // The person's hand on it, the agent told with its next message
        string? told = null;
        test.Run(() =>
        {
            noonPlanOpened = null;
            SetStepState(chat!, chat!.Plan!.Items.First(item => item.Id == "n1"), HexPlanState.Done);
            noonPlanAdd!.Text = "Check the plan with the person";
            AddPlanStep(chat);
            EditPlan(chat, plan => plan.Moved("n3", -1), () => "moved n3 up");
        });
        test.Settle(0.5f);
        test.Check("noon plan: the person marking a step done stamps it, adding one puts it after the open steps, moving one swaps it with the open step before", () =>
        {
            HexPlanItem[] items = [.. chat!.Plan!.Items];
            int added = Array.FindIndex(items, item => item.Text == "Check the plan with the person");
            return items.First(item => item.Id == "n1") is { State: HexPlanState.Done, DoneAt: > 0 }
                && added == items.Length - 1 && items[added].State == HexPlanState.Pending
                && Array.FindIndex(items, item => item.Id == "n3") < Array.FindIndex(items, item => item.Id == "n2")
                && noonPlanAdd!.Text.Length == 0;
        });
        test.Check("noon plan: the head says the changes reach the agent with the next message", () => Shown(Said("noon.plan_told_next")) == 1);
        test.Run(() => told = TakePlanTold(chat));
        test.Check("noon plan: what the person changed goes in front of the next message, once", () =>
            told is { } said && said.Contains("n1", StringComparison.Ordinal) && said.Contains("marked done", StringComparison.Ordinal)
            && said.Contains("Check the plan with the person", StringComparison.Ordinal) && said.Contains("moved n3 up", StringComparison.Ordinal)
            && said.Contains("hex_plan_read", StringComparison.Ordinal) && TakePlanTold(chat) is null);

        // What's typed in the box to add a step stays through a redraw, the agents moving drew the tab again
        test.Run(() =>
        {
            noonPlanAdd!.Text = "half typed";
            RedrawPlan();
        });
        test.Settle(0.5f);
        test.Check("noon plan: the box adding a step keeps what's typed as the tab is drawn again", () =>
            noonPlanAdd!.Text == "half typed" && Descendants(noonPlanRows).Contains(noonPlanAdd));
        test.Run(() => noonPlanAdd!.Text = string.Empty);

        // A new task puts the old one's steps done aside, folded under the new plan
        test.Run(() =>
        {
            chat!.SetPlan(HexAgentPlan.NewTask(chat.Plan, "Menus revamp", [new HexPlanItem("Read the menus", HexPlanState.Doing, "m1"), new HexPlanItem("Group the items", HexPlanState.Pending, "m2")],
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            RedrawPlan();
        });
        test.Settle(0.5f);
        test.Check("noon plan: a new task's plan isn't drowned by the last one's, its steps done folded as an earlier task", () =>
            Shown("Menus revamp") == 1 && Shown("Read the menus") == 1 && Shown("Finished step") == 0
            && Labelled(Said("noon.plan_earlier_one")) is not null && chat!.Plan!.Earlier is [{ Title: "Dawn revamp and text crispness", Items.Count: 41 }]);
        test.Run(() =>
        {
            noonPlanEarlierShown = true;
            RedrawPlan();
        });
        test.Settle(0.5f);
        test.Run(() => Shot("noon-plan-new-task"));
        test.Settle(0.4f);
        test.Run(() =>
        {
            noonPlanEarlierShown = false;
            ShowStage(NoonStage.Chat);
            agentChats.Remove(chat!);
        });
        test.Settle(0.3f);
    }
}
