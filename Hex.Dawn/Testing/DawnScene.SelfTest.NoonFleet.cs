using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Agentic's fleet. A chat that started two others is a block of three, the two under it a step in, the totals over the
// fleet count them and the one working, a card says what it's doing, and closing the one at the top closes the family.
internal sealed partial class DawnScene
{
    private void TestNoonFleet(HexPointerScript test)
    {
        HexAgentChat? lead = null, scout = null, builder = null;

        test.Run(() =>
        {
            lead = new HexAgentChat(++agentChatsMade, "noon_agent") { Model = "claude-opus-5-5" };
            lead.Ask("coordinate the fleet test");
            scout = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = lead.Id, Depth = 1, Name = "Scout" };
            scout.Ask("look round");
            scout.Done();
            builder = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = lead.Id, Depth = 1, Name = "Builder" };
            builder.Ask("build it");
            builder.Done();
            agentChats.Add(lead);
            agentChats.Add(scout);
            agentChats.Add(builder);
            OpenAgentChat(lead);
            ShowFace(HexFace.Noon);
            noonWait = 0;
        });
        test.Settle(0.5f);

        // The card of a chat in the fleet, by where the chat is in its order
        UIComponent? CardOf(HexAgentChat chat)
        {
            int at = ChatsInOrder().FindIndex(each => each.Chat == chat);
            return at >= 0 && at < noonFleetRows.Children.Count ? noonFleetRows.Children[at] : null;
        }

        // The latest of them to finish first, as every band is the most recently active first
        test.Check("noon fleet: the two a chat started come right after it in the fleet", () =>
            ChatsInOrder().Select(each => each.Chat).SkipWhile(chat => chat != lead).Take(3).SequenceEqual([lead!, builder!, scout!]));
        test.Check("noon fleet: the ones it started are a step in under it", () =>
            CardOf(lead!) is { } top && CardOf(scout!) is { } first && CardOf(builder!) is { } second
            && first.Padding.Left > top.Padding.Left + 4 && MathF.Abs(second.Padding.Left - first.Padding.Left) < 1);
        test.Check("noon fleet: the cards say who they are", () =>
            CardOf(scout!) is { } card && Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Scout", StringComparison.Ordinal)));
        test.Check("noon fleet: the totals over the fleet count every agent and the one working", () =>
            noonFleetSummary.Text.Contains($"{agentChats.Count} agents", StringComparison.Ordinal) && noonFleetSummary.Text.Contains("1 running", StringComparison.Ordinal));

        test.Run(() =>
        {
            lead!.Done();
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon fleet: and none once it's done", () => noonFleetSummary.Text.Contains("0 running", StringComparison.Ordinal));

        // The one that started them clears one and deletes the other, which nobody else may
        Task<McpToolResult>? stranger = null, cleared = null, deleted = null;
        test.Run(() =>
        {
            scout!.Say("what the scout found");
            scout.Session = "scout-session";
            stranger = McpTools.CallAs("hex_agent_close", new System.Text.Json.Nodes.JsonObject { ["id"] = scout.Id }, CancellationToken.None, builder!.Id);
            cleared = McpTools.CallAs("hex_agent_close", new System.Text.Json.Nodes.JsonObject { ["id"] = scout.Id, ["clear"] = true }, CancellationToken.None, lead!.Id);
        });
        test.WaitFor(() => stranger?.IsCompleted == true && cleared?.IsCompleted == true, 10.0f);
        test.Check("noon fleet: only the one that started an agent closes or clears it", () =>
            stranger is { IsCompletedSuccessfully: true, Result.IsError: true } && ChatById(scout!.Id) is not null);
        test.Check("noon fleet: cleared, the chat stays, emptied, its session gone", () =>
            cleared is { IsCompletedSuccessfully: true, Result.IsError: false } && ChatById(scout!.Id) == scout
            && scout.Entries.Count == 0 && scout.Session is null);
        test.Run(() => deleted = McpTools.CallAs("hex_agent_close", new System.Text.Json.Nodes.JsonObject { ["id"] = builder!.Id }, CancellationToken.None, lead!.Id));
        test.WaitFor(() => deleted?.IsCompleted == true, 10.0f);
        test.Check("noon fleet: closed, the chat is gone from the fleet", () =>
            deleted is { IsCompletedSuccessfully: true, Result.IsError: false } && ChatById(builder!.Id) is null
            && !ChatsInOrder().Any(each => each.Chat == builder));

        test.Run(() => CloseAgentChat(lead!));
        test.Settle(0.4f);
        test.Check("noon fleet: closing the one at the top closes the ones it started", () =>
            ChatById(lead!.Id) is null && ChatById(scout!.Id) is null && ChatById(builder!.Id) is null);
        test.Run(() => ShowFace(HexFace.Dawn));
        test.Settle(0.2f);

        TestNoonFleetFinished(test);
        if (OperatingSystem.IsWindows())
            TestNoonFleetSpawned(test);
    }

    // The finished folded away under a group of their own below the working, in the fleet and the side bar's list alike:
    // a chat read back from an earlier run is finished at once, one that just ended stays up with the working a while,
    // the group opens and folds and is remembered, the chat open is never hidden and the keys go round what's on screen
    private void TestNoonFleetFinished(HexPointerScript test)
    {
        HexAgentChat? old = null, just = null, working = null;
        bool wasOpen = chatsFinishedOpen;
        bool HasHead(UIComponent rows) => Descendants(rows).OfType<Button>().Any(button => button.Label.Contains(Said("agents_panel.finished_head", ("count", "1")), StringComparison.Ordinal));

        test.Run(() =>
        {
            chatsFinishedOpen = false;
            old = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Old finished" };
            old.Restore("from before", [new HexAgentEntry(HexAgentEntryKind.User, "look at the old thing"), new HexAgentEntry(HexAgentEntryKind.Text, "done long ago")]);
            just = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Just finished" };
            just.Ask("a quick one");
            just.Done();
            working = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "Still working" };
            working.Ask("keep going");
            agentChats.Add(old);
            agentChats.Add(just);
            agentChats.Add(working);
            OpenAgentChat(working);
            ShowFace(HexFace.Noon);
            noonWait = 0;
        });
        test.Settle(0.5f);
        test.Check("noon fleet finished: working first, the one that just ended under it, the old one folded away", () =>
            FleetShown().Select(each => each.Chat).Where(chat => chat == old || chat == just || chat == working).SequenceEqual([working!, just!])
            && ChatsInOrder().Select(each => each.Chat).Last() == old && chatsFinished.Count == 1);
        test.Check("noon fleet finished: the folded group's head is under the cards, one row for each card besides", () =>
            HasHead(noonFleetRows) && noonFleetRows.Children.Count == FleetShown().Count + 1);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "fleet_finished_folded.png"));
        });
        test.Wait();

        test.Run(() =>
        {
            OpenAgentChat(working!);
            StepAgent(1);
        });
        test.Check("noon fleet finished: the next agent key skips what's folded", () => agentChat != old);

        test.Run(() =>
        {
            ToggleFinished();
            noonWait = 0;
        });
        test.Settle(0.5f);
        test.Check("noon fleet finished: opened, the group shows its chats under its head", () =>
            chatsFinishedOpen && FleetShown().Any(each => each.Chat == old) && noonFleetRows.Children.Count == FleetShown().Count && HasHead(noonFleetRows));
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "fleet_finished_open.png"));
        });
        test.Wait();

        test.Run(() =>
        {
            ToggleFinished();
            OpenAgentChat(old!);
            noonWait = 0;
        });
        test.Settle(0.5f);
        test.Check("noon fleet finished: the chat open is never folded away", () =>
            !chatsFinishedOpen && FleetShown().Any(each => each.Chat == old));

        test.Run(() =>
        {
            OpenAgentChat(working!);
            ShowFace(HexFace.Dawn);
            ShowAgentsPanel();
            agentsListOpen = true;
            agentsListDrawn = NOT_DRAWN;
        });
        test.Settle(0.5f);
        test.Check("noon fleet finished: the side bar's list folds them the same", () =>
            HasHead(agentsListRows) && !Descendants(agentsListRows).OfType<AgentCardButton>().Any(pick => pick.Chat == old)
            && Descendants(agentsListRows).OfType<AgentCardButton>().Any(pick => pick.Chat == just));
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "list_finished_folded.png"));
        });
        test.Wait();

        test.Run(() =>
        {
            StopAgent(working);
            foreach (HexAgentChat chat in (HexAgentChat?[])[old, just, working])
                if (chat is not null)
                    CloseAgentChat(chat);
            chatsFinishedOpen = wasOpen;
        });
        test.Settle(0.2f);
    }

    // A stand-in agent system whose agent is ping, a few seconds of lines and gone (cmd waited for good on the --version
    // the systems panel asks every agent program), two of them started by a chat through
    // hex_agent_spawn as an agent would, shown under it while they run, and stopped with it
    private void TestNoonFleetSpawned(HexPointerScript test)
    {
        HexExtension? standIn = null;
        HexAgentChat? lead = null;
        Task<McpToolResult>? first = null, second = null;
        HexAgentChat[] Started() => lead is null ? [] : [.. agentChats.Where(chat => chat.Parent == lead.Id)];

        test.Run(() =>
        {
            string folder = Path.Combine(testFolder, "noon_stand_in");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, HexExtension.FILE), """
                let extension = { name: "Noon stand-in" };
                func on_start(hex) {
                    hex.agent({ id: "noon_stand_in", name: "Stand-in", run: { program: "ping",
                        arguments: (prompt, chat) => ["-n", "6", "127.0.0.1"] } });
                }
                """);
            standIn = new HexExtension(folder, builtIn: false) { Enabled = true };
            extensions.Add(standIn);
            extensions.Start(standIn);
            lead = new HexAgentChat(++agentChatsMade, "noon_stand_in");
            lead.Ask("start two helpers");
            agentChats.Add(lead);
            ShowFace(HexFace.Noon);
            OpenAgentChat(lead);
        });
        test.Settle(0.2f);
        test.Run(() =>
        {
            first = McpTools.CallAs("hex_agent_spawn", new System.Text.Json.Nodes.JsonObject { ["prompt"] = "scout the tests", ["name"] = "Scout", ["agent"] = "noon_stand_in" }, CancellationToken.None, lead!.Id);
            second = McpTools.CallAs("hex_agent_spawn", new System.Text.Json.Nodes.JsonObject { ["prompt"] = "build it", ["name"] = "Builder", ["agent"] = "noon_stand_in" }, CancellationToken.None, lead!.Id);
        });
        test.WaitFor(() => first?.IsCompleted == true && second?.IsCompleted == true, 15.0f);
        test.Run(() => noonWait = 0);
        test.Settle(0.5f);
        test.Check("noon fleet: two agents a chat started with hex_agent_spawn run under it in the fleet", () =>
            first is { IsCompletedSuccessfully: true, Result.IsError: false } && second is { IsCompletedSuccessfully: true, Result.IsError: false }
            && Started() is { Length: 2 } started && started.All(chat => chat.Running)
            && ChatsInOrder().Select(each => each.Chat).SkipWhile(chat => chat != lead).Skip(1).Take(2).All(chat => chat.Parent == lead!.Id));

        // Stopping its turn leaves them working, the person stopping it to say something is still waiting for them
        test.Run(() => StopAgent(lead));
        test.Settle(0.5f);
        test.Check("noon fleet: stopping the one that started them stops its turn and leaves them running", () => Started() is { Length: 2 } started && started.All(chat => chat.Running));

        test.Run(() => StopAgent(lead, withStarted: true));
        test.WaitFor(() => Started().All(chat => !chat.Running), 10.0f);
        test.Check("noon fleet: stopping it with its agents stops them", () => Started() is { Length: 2 } started && started.All(chat => !chat.Running));

        test.Run(() =>
        {
            CloseAgentChat(lead!);
            ShowFace(HexFace.Dawn);
            // Started for the agents, its server would keep the test's Hex from closing
            StopMcp();
            if (standIn is not null)
            {
                extensions.ForgetAgents(standIn);
                extensions.Remove(standIn);
            }
        });
        test.Settle(0.2f);
    }
}
