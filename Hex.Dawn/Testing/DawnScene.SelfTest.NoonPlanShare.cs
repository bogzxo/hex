using System.Text.Json.Nodes;

namespace Hex.Dawn;

// The Plan tab's tool as an agent patches it (IDs it didn't give, an owner cleared with null), what a chat's own connection
// is told and offered that another isn't, and two chats started apart sharing one checkout: A and B both mean to change the
// same file, B is told A has it, the two talk though they're of different families, B takes other work, and once A is done
// B is told the file is free and takes it up.
internal sealed partial class DawnScene
{
    private void NoonPlanPatches(HexPointerScript test, Func<HexAgentChat> chatOf, Action<string, string> call, Func<string> reply)
    {
        call("hex_plan_update", """{"merge":false,"items":[{"text":"Alpha"},{"text":"Beta"}]}""");
        test.Check("noon: rows written without IDs get them, and the reply says them", () =>
            reply().Contains("Changed: s1 pending, s2 pending", StringComparison.Ordinal)
            && chatOf().Plan?.Items.Select(item => item.Id).SequenceEqual(["s1", "s2"]) == true);

        call("hex_plan_update", """{"items":[{"id":"s1","text":"Alpha renamed","agent":5}]}""");
        call("hex_plan_update", """{"items":[{"id":"s1","agent":null,"state":"doing"}]}""");
        call("hex_plan_update", """{"items":[{"id":"s1","agent":null,"state":"doing"}]}""");
        test.Check("noon: a row renamed by its returned ID, its owner cleared with null, the update retried, still two rows", () =>
            chatOf().Plan is { Items.Count: 2 } plan && plan.Items[0] is { Id: "s1", Text: "Alpha renamed", Agent: null, State: HexPlanState.Doing }
            && reply().Contains("Changed: s1 doing", StringComparison.Ordinal));

        McpServer? own = null, other = null;
        Task<JsonNode?>?[] answers = new Task<JsonNode?>?[4];
        test.Run(() =>
        {
            own = MakeMcpServer(chatOf().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            other = MakeMcpServer(null);
            JsonObject Ask(string method) => new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = new JsonObject() };
            answers[0] = own.Handle(Ask("initialize"));
            answers[1] = other.Handle(Ask("initialize"));
            answers[2] = own.Handle(Ask("tools/list"));
            answers[3] = other.Handle(Ask("tools/list"));
        });
        test.WaitFor(() => answers.All(answer => answer?.IsCompleted == true), 5);
        test.Check("noon: a chat's own connection is told how its Plan tab is kept and offered hex_plan_update, another is not", () =>
        {
            string[] said = [.. answers.Select(answer => answer?.Result?.ToJsonString() ?? string.Empty)];
            return said[0].Contains("Plan tab", StringComparison.Ordinal) && !said[1].Contains("Plan tab", StringComparison.Ordinal)
                && said[2].Contains("\"hex_plan_update\"", StringComparison.Ordinal) && !said[3].Contains("\"hex_plan_update\"", StringComparison.Ordinal)
                && said[3].Contains("\"hex_plan_read\"", StringComparison.Ordinal);
        });
    }

    private void NoonPlanCoordination(HexPointerScript test, Func<HexAgentChat> chatOf)
    {
        HexAgentChat? b = null;
        McpServer? serverA = null, serverB = null;
        test.Run(() =>
        {
            // Started by the person, not by A, a family of its own in the same checkout
            b = new HexAgentChat(++agentChatsMade, "noon_agent") { Name = "B" };
            b.Ask("fix the parser");
            b.Done();
            agentChats.Add(b);
            serverA = MakeMcpServer(chatOf().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            serverB = MakeMcpServer(b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        // A call's text and whether it failed, kept by name for the checks after it
        Dictionary<string, (string Text, bool Error)> got = [];
        void Call(string key, Func<McpServer> server, string name, Func<string> args)
        {
            Task<JsonNode?>? answer = null;
            test.Run(() => answer = server().Handle(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(args()) }
            }));
            test.WaitFor(() => answer?.IsCompleted == true, 5);
            test.Run(() =>
            {
                JsonNode? result = answer?.Result?["result"];
                got[key] = (result?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty, result?["isError"]?.GetValue<bool>() == true);
            });
        }

        string A() => chatOf().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string B() => b!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Call("early", () => serverB!, "hex_agent_message", () => $$"""{"to":{{A()}},"text":"hi"}""");
        test.Check("noon: two chats of different families can't message each other before they share a file", () => got["early"].Error);

        Call("a", () => serverA!, "hex_plan_update", () => """{"merge":false,"items":[{"id":"edit","text":"Edit the parser","state":"doing","files":["src/Parser.cs"]}]}""");
        Call("b", () => serverB!, "hex_plan_update",
            () => """{"merge":false,"items":[{"id":"parser","text":"Fix the parser","files":["./src/parser.cs"]},{"id":"docs","text":"Write the docs","state":"doing"}]}""");
        test.Check("noon: B advertising the same file hears A has it in a doing step", () =>
            !got["b"].Error && got["b"].Text.Contains($"Also named by #{A()}", StringComparison.Ordinal) && got["b"].Text.Contains("src/parser.cs", StringComparison.Ordinal));

        Call("list", () => serverB!, "hex_agent_list", () => "{}");
        test.Check("noon: every chat sees A's doing step and files in hex_agent_list", () =>
            got["list"].Text.Contains("doing \"Edit the parser\", files src/Parser.cs", StringComparison.Ordinal));

        Call("ask", () => serverB!, "hex_agent_message", () => $$"""{"to":{{A()}},"kind":"question","text":"You have src/Parser.cs, I will take the docs first"}""");
        Call("order", () => serverB!, "hex_agent_message", () => $$"""{"to":{{A()}},"kind":"instruction","text":"stop"}""");
        Call("answer", () => serverA!, "hex_agent_message", () => $$"""{"to":{{B()}},"kind":"answer","text":"Go ahead after I am done"}""");
        test.Check("noon: sharing a file, B can ask A who goes first and A answer, but neither instruct the other", () =>
            !got["ask"].Error && got["order"].Error && !got["answer"].Error);

        Call("done", () => serverA!, "hex_plan_update", () => """{"items":[{"id":"edit","state":"done"}]}""");
        test.Check("noon: A finishing its step tells B the file is free", () =>
            got["done"].Text.Contains($"Told #{B()}", StringComparison.Ordinal)
            && b!.Mail.Any(message => message.From == chatOf().Id && message.Kind == "progress" && message.Text.Contains("src/parser.cs", StringComparison.OrdinalIgnoreCase)));

        Call("resume", () => serverB!, "hex_plan_update", () => """{"items":[{"id":"docs","state":"done"},{"id":"parser","state":"doing"}]}""");
        test.Check("noon: B takes the file up with no other claim on it", () =>
            !got["resume"].Error && !got["resume"].Text.Contains("Also named", StringComparison.Ordinal)
            && b!.Plan?.Doing?.Id == "parser" && PlanPrompt(b, "go on").Contains("doing parser", StringComparison.Ordinal));

        test.Run(() => agentChats.Remove(b!));
    }
}
