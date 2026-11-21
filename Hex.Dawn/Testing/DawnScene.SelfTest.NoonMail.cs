using System.Text.Json.Nodes;

using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// What a family's agents say to each other in Agentic. Two chats of a family send each other messages through
// hex_agent_message as their agents would, Details lists them with a way to the whole of it, and the family's window has
// every message in order, filtered by kind and by member, with only the rows in sight made however many there are.
internal sealed partial class DawnScene
{
    private void TestNoonMail(HexPointerScript test)
    {
        HexAgentChat? lead = null, scout = null;
        Task<McpToolResult>? first = null, second = null;

        test.Run(() =>
        {
            lead = new HexAgentChat(++agentChatsMade, "noon_agent");
            lead.Ask("coordinate the mail test");
            lead.Done();
            scout = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = lead.Id, Depth = 1, Name = "Scout" };
            scout.Ask("look round");
            scout.Done();
            agentChats.Add(lead);
            agentChats.Add(scout);
        });

        // As the agents would, the scout to the one that started it and back
        test.Run(() => first = McpTools.CallAs("hex_agent_message", new JsonObject { ["text"] = "the server starts in StartMcp", ["kind"] = "finding" }, CancellationToken.None, scout!.Id));
        test.WaitFor(() => first?.IsCompleted == true, 10.0f);
        test.Run(() => second = McpTools.CallAs("hex_agent_message", new JsonObject { ["to"] = scout!.Id, ["text"] = "where are the tests?", ["kind"] = "question" }, CancellationToken.None, lead!.Id));
        test.WaitFor(() => second?.IsCompleted == true, 10.0f);
        test.Check("noon mail: a message from one agent of a family to another goes through", () =>
            first is { IsCompletedSuccessfully: true, Result.IsError: false } && second is { IsCompletedSuccessfully: true, Result.IsError: false }
            && hub.MailOf(lead!).All.Count == 2);

        test.Run(() =>
        {
            hub.MailOf(lead!).Add(new HexAgentMail(hub.NextMailSeq(), null, "the person watching", "instruction", "look at the tests first", true) { To = lead!.Id }, HexMailRoute.Inbox);
            ShowFace(HexFace.Noon);
            OpenAgentChat(lead!);
            noonWait = 0;
            noonContextDrawn = CONTEXT_NOT_DRAWN;
        });
        test.Settle(0.4f);
        Button? every = null;
        test.Run(() => every = Descendants(noonContextRows).OfType<Button>().FirstOrDefault(button => button.Label.Contains(Said("noon.family_every", ("count", "3")), StringComparison.Ordinal)));
        test.Check("noon mail: Details list the family's messages with a way to all of them", () =>
            every is not null && Descendants(noonContextRows).OfType<Label>().Any(label => label.Text.Contains("where are the tests?", StringComparison.Ordinal)));

        // Each row's words, the last label of it under the line of who to whom
        string[] Shown() => familyList is null ? [] : [.. familyList.Children.OrderByDescending(row => row.Bounds.Max.Y).Select(row => Descendants(row).OfType<Label>().LastOrDefault()?.Text).OfType<string>()];
        test.Run(() => every?.OnPressed?.Invoke());
        test.Settle(0.4f);
        test.Check("noon mail: the family's window has every message, oldest first", () =>
            familyWindow is not null && familyList is { ItemCount: 3 } && Shown() is { Length: 3 } said
            && said[0].Contains("StartMcp", StringComparison.Ordinal) && said[1].Contains("where are the tests?", StringComparison.Ordinal)
            && said[2].Contains("look at the tests first", StringComparison.Ordinal));
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "noon-mail.png"));
        });

        // By kind, and by member, what one sent or was sent
        // A chip pressed by the words on it, the kinds the summary's next row and the members the last
        void PressChip(bool members, string starts)
        {
            StackPanel row = members ? familyFilters!.Children.OfType<StackPanel>().Last() : familyFilters!.Children.OfType<StackPanel>().ElementAt(1);
            Panel chip = row.Children.OfType<Panel>().First(each => each.Children.OfType<Label>().Any(label => label.Text.Contains(starts, StringComparison.Ordinal)));
            chip.Children.OfType<Button>().First().OnPressed!();
        }

        test.Check("noon mail: the summary counts the messages and the chips how many each lets through", () =>
            Descendants(familyFilters!).OfType<Label>().Any(label => label.Text.Contains(Said("noon.family_messages"), StringComparison.Ordinal))
            && Descendants(familyFilters!).OfType<Label>().Any(label => label.Text.Contains($"{Said("noon.family_finding")}  1", StringComparison.Ordinal)));
        // Pressed with the pointer, the way the person does, the list laid over the chips once took every press on them
        test.Click(() => chrome.ToWorld(familyFilters!.Children.OfType<StackPanel>().ElementAt(1).Children.OfType<Panel>()
            .First(each => each.Children.OfType<Label>().Any(label => label.Text.Contains(Said("noon.family_finding"), StringComparison.Ordinal))).Bounds.Center));
        test.Settle(0.3f);
        test.Check("noon mail: a chip pressed with the pointer filters the window", () => familyKind == "finding");
        test.Check("noon mail: the findings filter leaves the finding", () => familyList is { ItemCount: 1 } && Shown() is [var only] && only.Contains("StartMcp", StringComparison.Ordinal));
        test.Run(() => PressChip(false, Said("noon.family_question")));
        test.Settle(0.3f);
        test.Run(() => PressChip(false, Said("noon.family_instruction")));
        test.Settle(0.3f);
        test.Run(() => PressChip(true, $"#{scout!.Id} "));
        test.Settle(0.3f);
        test.Check("noon mail: filtered to nothing the window says so with a way back", () =>
            familyList is { ItemCount: 0 } && familyEmpty is { Visible: true } && Descendants(familyEmpty).OfType<Button>().Any(button => button.Label == Said("noon.family_show_all")));
        test.Run(() => PressChip(false, Said("noon.family_all")));
        test.Settle(0.3f);
        test.Check("noon mail: a member's filter leaves what it sent and was sent", () => familyList is { ItemCount: 2 });

        // An agent's number on a message opens its chat
        test.Run(() => Descendants(familyList!).OfType<Button>().First(button => button.Label.StartsWith($"#{scout!.Id} ", StringComparison.Ordinal)).OnPressed!());
        test.Settle(0.3f);
        test.Check("noon mail: an agent's number on a message opens its chat", () => agentChat == scout && noonStage == NoonStage.Chat);
        test.Run(() => OpenAgentChat(lead!));

        // Hundreds of messages, the family keeping its last 50, the instruction its agent hasn't read among them however
        // old, a few rows made and the newest in sight
        test.Run(() =>
        {
            PressChip(true, Said("noon.family_everyone"));
            for (int i = 1; i <= 500; i++)
            {
                var step = new HexAgentMail(hub.NextMailSeq(), scout!.Id, "Scout", "progress", $"step {i} of the long job", false) { To = lead!.Id };
                lead.Post(step);
                hub.MailOf(lead).Add(step, HexMailRoute.Inbox);
            }
        });
        test.Settle(0.5f);
        test.Check("noon mail: of hundreds of messages the family keeps its last 50, the unread instruction kept", () =>
            hub.MailOf(lead!).All.Count == HexMailLog.MOST && lead!.Mail.Count == HexAgentChat.MOST_MAIL
            && hub.MailOf(lead).All.Any(entry => entry.Mail.Text == "look at the tests first")
            && hub.MailOf(lead).All[^1].Mail.Text == "step 500 of the long job"
            && !hub.MailOf(lead).All.Any(entry => entry.Mail.Text == "step 1 of the long job"));
        test.Check("noon mail: only those near what's in sight have rows, the newest among them", () =>
            familyList is { ItemCount: HexMailLog.MOST } list && list.Children.Count is > 0 and < HexMailLog.MOST
            && Shown().Any(said => said.Contains("step 500 of the long job", StringComparison.Ordinal)));

        // Details shows the newest few, the older folded into a row that opens them in place and folds them again,
        // open or folded kept for each chat
        int Steps() => Descendants(noonContextRows).OfType<Label>().Count(label => label.Text.Contains("of the long job", StringComparison.Ordinal));
        Button? Fold() => Descendants(noonContextRows).OfType<Button>().FirstOrDefault(button =>
            button.Label.StartsWith(Said("noon.family_more", ("count", string.Empty))[..4], StringComparison.Ordinal) || button.Label == Said("noon.family_fewer"));
        test.Run(() =>
        {
            noonContextDrawn = CONTEXT_NOT_DRAWN;
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon mail: Details shows the newest five, the rest folded into More", () =>
            Steps() == 5 && Fold() is { } more && more.Label == Said("noon.family_more", ("count", (HexMailLog.MOST - 5).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            && Descendants(noonContextRows).OfType<Label>().Any(label => label.Text.Contains("step 500 of the long job", StringComparison.Ordinal)));
        test.Run(() => Fold()!.OnPressed!());
        test.Settle(0.4f);
        test.Check("noon mail: More opens the older in place, with a way to fold them", () =>
            Steps() > 5 && Fold() is { } fewer && fewer.Label == Said("noon.family_fewer"));
        test.Check("noon mail: the filters and the way to the whole conversation are still there", () =>
            Descendants(noonContextRows).OfType<Button>().Any(button => button.Label.Contains(Said("noon.family_every", ("count", HexMailLog.MOST.ToString(System.Globalization.CultureInfo.InvariantCulture))), StringComparison.Ordinal)));
        test.Run(() =>
        {
            OpenAgentChat(scout!);
            noonContextDrawn = CONTEXT_NOT_DRAWN;
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon mail: another chat of the family has its own fold, folded", () => Steps() == 5);
        test.Run(() =>
        {
            OpenAgentChat(lead!);
            noonContextDrawn = CONTEXT_NOT_DRAWN;
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon mail: and coming back the chat's own is still open", () => Steps() > 5);
        test.Run(() => Fold()!.OnPressed!());
        test.Settle(0.4f);
        test.Check("noon mail: Fewer folds them again", () => Steps() == 5);

        // What the scout asks leave to do goes to the one that started it, which decides it with hex_agent_approve
        bool? decided = null;
        Task<McpToolResult>? stranger = null, approved = null;
        test.Run(() =>
        {
            scout!.Approval("Run git push", "git push origin development", allowed => decided = allowed);
            stranger = McpTools.CallAs("hex_agent_approve", new JsonObject { ["id"] = scout.Id, ["allow"] = true }, CancellationToken.None, scout.Id);
        });
        test.WaitFor(() => stranger?.IsCompleted == true, 10.0f);
        test.Check("noon mail: what an agent asks leave to do reaches the one that started it as a question it answers with hex_agent_approve", () =>
            hub.MailOf(lead!).All.Any(entry => entry.Mail.Kind == "question" && entry.Mail.To == lead!.Id
                && entry.Mail.Text.Contains("Run git push", StringComparison.Ordinal) && entry.Mail.Text.Contains("hex_agent_approve", StringComparison.Ordinal)));
        test.Check("noon mail: only the one that started it decides, another chat is refused", () =>
            stranger is { IsCompletedSuccessfully: true, Result.IsError: true } && decided is null);
        test.Run(() => approved = McpTools.CallAs("hex_agent_approve", new JsonObject { ["id"] = scout!.Id, ["allow"] = false, ["request"] = 1 }, CancellationToken.None, lead!.Id));
        test.WaitFor(() => approved?.IsCompleted == true, 10.0f);
        test.Check("noon mail: and its answer is the agent's, the card answered with it", () =>
            approved is { IsCompletedSuccessfully: true, Result.IsError: false } && decided == false && !scout!.Pending.Any());


        // The child stays running while the waiter receives a whole batch from the editor's next pump.
        Task<McpToolResult>? waiting = null;
        test.Run(() =>
        {
            lead!.MailRead(lead.Mail[^1].Seq);
            lead.Ask("Wait for the scout");
            scout!.Ask("Keep working");
            PublishAgentStatuses();
            waiting = McpTools.CallAs("hex_agent_wait",
                new JsonObject { ["mode"] = "any", ["messages"] = false, ["timeout"] = 30d }, CancellationToken.None, lead.Id);
        });
        test.WaitFor(() => AgentWaiting(lead!), 5.0f);
        test.Run(() => Deliver(lead!, scout, "progress", "the scout is still scanning", directive: false));
        test.Settle(0.35f);
        test.Check("noon mail: progress alone leaves a messages=false wait running", () => waiting is { IsCompleted: false });

        test.Run(() =>
        {
            Deliver(lead!, scout, "finding", "a finding from the same tick", directive: false);
            DeliverFromPerson(lead!, "please read the tests first");
            Deliver(lead!, scout, "progress", "the last message in the tick", directive: false);
        });
        test.WaitFor(() => waiting?.IsCompleted == true, 5.0f);
        test.Check("noon mail: the person wakes an any wait and its reply includes the full batch", () =>
        {
            if (waiting is not { IsCompletedSuccessfully: true, Result.IsError: false } || scout is not { Running: true })
                return false;
            string reply = waiting.Result.AllText();
            int progress = reply.IndexOf("the scout is still scanning", StringComparison.Ordinal);
            int finding = reply.IndexOf("a finding from the same tick", StringComparison.Ordinal);
            int last = reply.IndexOf("the last message in the tick", StringComparison.Ordinal);
            return reply.StartsWith("Messages for you:\n\n[instruction from the person watching] please read the tests first", StringComparison.Ordinal)
                && progress > 0 && finding > progress && last > finding && lead!.UnreadMail().Length == 0;
        });

        test.Run(() => waiting = McpTools.CallAs("hex_agent_wait",
            new JsonObject { ["mode"] = "all", ["messages"] = false, ["timeout"] = 30d }, CancellationToken.None, lead!.Id));
        test.WaitFor(() => AgentWaiting(lead!), 5.0f);
        test.Run(() => Deliver(lead!, scout, "question", "which tests should I run?", directive: false));
        test.WaitFor(() => waiting?.IsCompleted == true, 5.0f);
        test.Check("noon mail: an addressed question wakes an all wait without repeating read messages", () =>
            waiting is { IsCompletedSuccessfully: true, Result.IsError: false } && scout is { Running: true }
            && waiting.Result.AllText().Contains("which tests should I run?", StringComparison.Ordinal)
            && !waiting.Result.AllText().Contains("please read the tests first", StringComparison.Ordinal));

        test.Run(() =>
        {
            scout!.Done();
            lead!.Done();
            familyWindow?.OnClosed?.Invoke();
            CloseAgentChat(lead!);
            ShowFace(HexFace.Dawn);
        });
        test.Settle(0.2f);
        test.Check("noon mail: the window goes when it's closed", () => familyWindow is null && familyList is null);
    }
}
