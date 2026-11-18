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

        test.Check("noon fleet: the two a chat started come right after it in the fleet", () =>
            ChatsInOrder().Select(each => each.Chat).SkipWhile(chat => chat != lead).Take(3).SequenceEqual([lead!, scout!, builder!]));
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

        test.Run(() => CloseAgentChat(lead!));
        test.Settle(0.4f);
        test.Check("noon fleet: closing the one at the top closes the ones it started", () =>
            ChatById(lead!.Id) is null && ChatById(scout!.Id) is null && ChatById(builder!.Id) is null);
        test.Run(() => ShowFace(HexFace.Dawn));
        test.Settle(0.2f);

        if (OperatingSystem.IsWindows())
            TestNoonFleetSpawned(test);
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

        test.Run(() => StopAgent(lead));
        test.WaitFor(() => Started().All(chat => !chat.Running), 10.0f);
        test.Check("noon fleet: stopping the one that started them stops them", () => Started() is { Length: 2 } started && started.All(chat => !chat.Running));

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
