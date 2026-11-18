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
            noonContextDrawn = string.Empty;
        });
        test.Settle(0.4f);
        Button? every = null;
        test.Run(() => every = Descendants(noonContextRows).OfType<Button>().FirstOrDefault(button => button.Label.Contains(Said("noon.family_every", ("count", "3")), StringComparison.Ordinal)));
        test.Check("noon mail: Details list the family's messages with a way to all of them", () =>
            every is not null && Descendants(noonContextRows).OfType<Label>().Any(label => label.Text.Contains("where are the tests?", StringComparison.Ordinal)));

        string[] Shown() => familyList is null ? [] : [.. familyList.Children.OfType<Label>().OrderByDescending(label => label.Bounds.Max.Y).Select(label => label.Text)];
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
        test.Run(() => familyFilters!.Children.OfType<StackPanel>().First().Children.OfType<Button>().First(button => button.Label == Said("noon.family_finding")).OnPressed!());
        test.Settle(0.3f);
        test.Check("noon mail: the findings filter leaves the finding", () => familyList is { ItemCount: 1 } && Shown() is [var only] && only.Contains("StartMcp", StringComparison.Ordinal));
        test.Run(() =>
        {
            familyFilters!.Children.OfType<StackPanel>().First().Children.OfType<Button>().First().OnPressed!();
            familyFilters.Children.OfType<StackPanel>().Last().Children.OfType<Button>().First(button => button.Label.StartsWith($"#{scout!.Id}", StringComparison.Ordinal)).OnPressed!();
        });
        test.Settle(0.3f);
        test.Check("noon mail: a member's filter leaves what it sent and was sent", () => familyList is { ItemCount: 2 });

        // Hundreds of messages, a few rows made, the newest in sight
        test.Run(() =>
        {
            familyFilters!.Children.OfType<StackPanel>().Last().Children.OfType<Button>().First().OnPressed!();
            for (int i = 1; i <= 500; i++)
                hub.MailOf(lead!).Add(new HexAgentMail(hub.NextMailSeq(), scout!.Id, "Scout", "progress", $"step {i} of the long job", false) { To = lead!.Id }, HexMailRoute.Inbox);
        });
        test.Settle(0.5f);
        test.Check("noon mail: of hundreds of messages only those near what's in sight have rows, the newest among them", () =>
            familyList is { ItemCount: 503 } list && list.Children.Count is > 0 and < 80
            && Shown().Any(said => said.Contains("step 500 of the long job", StringComparison.Ordinal)));

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

        test.Run(() =>
        {
            familyWindow?.OnClosed?.Invoke();
            CloseAgentChat(lead!);
            ShowFace(HexFace.Dawn);
        });
        test.Settle(0.2f);
        test.Check("noon mail: the window goes when it's closed", () => familyWindow is null && familyList is null);
    }
}
