using Hex.Shell;

using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Agentic's states after its revamp. The fleet in groups, what needs the person over what's working over the rest, each
// family kept together. A request waiting pinned over the box with Allow and Deny wherever the chat is scrolled to, and
// how many others wait. Closing a chat that's working asks first. An error with Try again and Copy. A plan the agent
// stopped on has Go. The permission mode a picker of its own. The running blue readable on a light look.
internal sealed partial class DawnScene
{
    private void TestNoonStates(HexPointerScript test)
    {
        HexAgentChat? idle = null, working = null, asking = null, failed = null, other = null;
        bool? allowed = null;

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            idle = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Idle one" };
            idle.Ask("done already");
            idle.Say("finished");
            idle.Done();
            working = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Working one" };
            working.Ask("keep going");
            asking = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Asking one" };
            asking.Ask("change the build");
            for (int i = 0; i < 40; i++)
            {
                asking.Tool("t" + i, "Read", $"{{\"file_path\":\"file{i}.cs\"}}");
                asking.ToolDone("t" + i, true, "Done");
            }
            asking.Approval("Run dotnet build", "dotnet build Hex.sln", answer => allowed = answer);
            failed = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Failed one" };
            failed.Ask("break");
            failed.Error("The agent stopped: something went wrong");
            failed.Done();
            agentChats.Add(idle);
            agentChats.Add(working);
            agentChats.Add(asking);
            agentChats.Add(failed);
            ShowFace(HexFace.Noon);
            OpenAgentChat(idle);
            noonWait = 0;
        });
        test.Settle(0.6f);

        // The fleet's groups
        test.Check("noon states: the chats that need you come first, then the working ones, then the rest", () =>
        {
            List<HexAgentChat> shown = FleetShown().ConvertAll(each => each.Chat);
            return shown.IndexOf(asking!) < shown.IndexOf(working!) && shown.IndexOf(failed!) < shown.IndexOf(working!) && shown.IndexOf(working!) < shown.IndexOf(idle!);
        });
        test.Check("noon states: each group has its heading, and still one row for each card", () =>
            noonFleetRows.Children.Count == FleetShown().Count
            && new[] { "noon.fleet_needs_you", "noon.fleet_working", "noon.fleet_rest" }.All(key => Descendants(noonFleetRows).OfType<Label>().Any(label => label.Text == Said(key))));
        test.Run(() => Shot("noon-states-fleet"));

        // Waiting on the person in another chat, the strip says how many and goes there
        test.Check("noon states: with another chat waiting, the strip over the box says so", () =>
            agentsWaitStrip is { Visible: true } strip && Descendants(strip).OfType<Button>().Any(button => button.Label == Said("agents_panel.waiting_others", ("count", "1"))));

        // The chat that asks, scrolled to its top, the request pinned over the box
        test.Run(() =>
        {
            OpenAgentChat(asking!);
            noonWait = 0;
        });
        test.Settle(0.6f);
        test.Run(() => agentsScroll.Offset = 0);
        test.Settle(0.3f);
        test.Check("noon states: the request waits over the box with Allow and Deny, scrolled to the top of the chat", () =>
            agentsWaitStrip is { Visible: true } strip && agentsScroll.Offset < 1
            && Descendants(strip).OfType<Label>().Any(label => label.Text.Contains("Run dotnet build", StringComparison.Ordinal))
            && Descendants(strip).OfType<Button>().Count(button => button.OnPressed is not null) >= 2
            && strip.Bounds.Min.Y >= agentsCompose.Bounds.Max.Y - 1);
        test.Check("noon states: the strip's buttons are at least 24 high and fit their words", () =>
            agentsWaitStrip is { } strip && Descendants(strip).OfType<Button>().All(button => button.Size.Y >= 24 && Fits(button)));
        test.Run(() => Shot("noon-states-waiting"));
        test.Run(() => Descendants(agentsWaitStrip!).OfType<Button>().First(button => button.Primary).OnPressed!());
        test.Settle(0.4f);
        test.Check("noon states: Allow on the strip decides it, and the strip goes", () => allowed == true && agentsWaitStrip is { Visible: false });

        // Closing a chat that's working asks first, Cancel keeps it
        Button? DialogButton(string label) => textDialog is { IsOpen: true } dialog ? Descendants(dialog).OfType<Button>().LastOrDefault(button => button.Label == label) : null;
        test.Run(() => AskCloseAgentChat(working!));
        test.Settle(0.3f);
        test.Check("noon states: closing a working chat asks first, saying it stops", () =>
            DialogButton(Said("agents_panel.close_go")) is not null && ChatById(working!.Id) is not null);
        test.Run(() => DialogButton(Said("git.discard_no"))?.OnPressed?.Invoke());
        test.Settle(0.3f);
        test.Check("noon states: and Cancel leaves it as it was", () => ChatById(working!.Id) == working && working.Running);
        test.Run(() => AskCloseAgentChat(idle!));
        test.Settle(0.2f);
        test.Check("noon states: a chat with nothing going closes at once", () => ChatById(idle!.Id) is null && DialogButton(Said("agents_panel.close_go")) is null);

        // An error that's the last of its chat says what to do about it
        test.Run(() =>
        {
            OpenAgentChat(failed!);
            noonWait = 0;
        });
        test.Settle(0.5f);
        test.Check("noon states: an error ends with Try again and Copy error", () =>
            Descendants(agentsScroll).OfType<Button>().Any(button => button.Label == Said("agents_panel.error_retry"))
            && Descendants(agentsScroll).OfType<Button>().Any(button => button.Label == Said("agents_panel.error_copy")));
        test.Check("noon states: a turn of no calls that took no time has no line of nothing under it", () =>
            !Descendants(agentsScroll).OfType<Label>().Any(label => label.Text.Contains("0 tool calls", StringComparison.Ordinal)));
        test.Check("noon states: with Details beside the chat, the chat's own line over it is away", () =>
            !noonContext.Visible || !agentsMeta.Visible);
        test.Run(() => Shot("noon-states-error"));

        // A plan the agent stopped on has Go
        test.Run(() =>
        {
            other = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Planner" };
            other.Ask("plan it first");
            other.Tool("edit-1", "Edit", """{"file_path":"src/Plan.cs","old_string":"a","new_string":"b"}""");
            other.ToolDone("edit-1", true, "Done");
            other.Say("Here's the plan, say go.");
            other.Done();
            other.SetPlan(new HexAgentPlan("Change it", [new HexPlanItem("Read the code", HexPlanState.Done, "s1"), new HexPlanItem("Change it", HexPlanState.Pending, "s2")]));
            agentChats.Add(other);
            OpenAgentChat(other);
            ShowStage(NoonStage.Plan);
            noonWait = 0;
            noonStageDrawn = NOT_DRAWN;
        });
        test.Settle(0.5f);
        test.Check("noon states: a plan waiting for its go has Go", () =>
            HexAgentPlan.Of(other) is { Items.Count: > 0 } && Descendants(noonPlanRows).OfType<Button>().Any(button => button.Label == Said("noon.plan_go") && button.Primary));
        test.Run(() => Shot("noon-states-plan"));
        test.Run(() =>
        {
            ShowStage(NoonStage.Chat);
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon states: a finished turn says its calls under its last row, its files a link to them", () =>
            Descendants(agentsScroll).OfType<Label>().Any(label => label.Text.StartsWith("$(check)", StringComparison.Ordinal) && label.Text.Contains("1 tool call", StringComparison.Ordinal))
            && Descendants(agentsScroll).OfType<Button>().Any(button => button.Label == Said("agents_panel.turn_files_one") && button.OnPressed is not null));
        test.Check("noon states: a tool's row has no bar down its side, how it went is its icon and its outline", () =>
            !Descendants(agentsScroll).OfType<Panel>().Any(panel => panel.Visible && panel.Size.X is > 0 and <= 3 && panel.Size.Y > 8));
        test.Run(() => Shot("noon-states-turn"));

        // The composer's pickers say plain words, a colour is the button's own (markup in a label was drawn as text)
        test.Check("noon states: no picker of the box has markup in its words, and none overlaps the next", () =>
        {
            Button[] pickers = [.. agentsOptions.Children.OfType<Button>()];
            return pickers.All(button => !button.Label.Contains("[color", StringComparison.Ordinal))
                && pickers.Zip(pickers.Skip(1)).All(pair => pair.First.Bounds.Max.X <= pair.Second.Bounds.Min.X + 0.5f)
                && pickers.All(button => button.Label.Length == 0 || Fits(button));
        });

        // The status bar and the fleet's line in the singular
        test.Check("noon states: one agent is said as one", () => Said("agents_item.agents_one") == "1 agent" && Said("noon.summary_one").StartsWith("1 agent ", StringComparison.Ordinal));

        // The running blue a light look can read
        test.Check("noon states: the running blue is deeper on a light look than on a dark one", () =>
        {
            var dark = RunningBlue(null);
            return dark.X > 0.3f && Skin is { } skin && (skin.Palette.LightField ? RUNNING_BLUE.Y < dark.Y : RUNNING_BLUE == dark);
        });

        test.Run(() =>
        {
            foreach (HexAgentChat chat in new[] { working, asking, failed, other })
            {
                if (chat is not null && ChatById(chat.Id) is not null)
                {
                    chat.Done();
                    CloseAgentChat(chat);
                }
            }
            ShowFace(HexFace.Dawn);
        });
        test.Settle(0.3f);

        // A row's text as the button measures it against its width
        bool Fits(Button row) => Skin is not { } skin
            || skin.Font.Measure($"$({row.Icon}) {row.Label}", row.LabelScale).X + skin.ButtonPadding.Total.X <= row.Size.X + 0.5f;
    }
}
