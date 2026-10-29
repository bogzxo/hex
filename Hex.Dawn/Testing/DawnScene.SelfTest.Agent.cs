using System.Numerics;
using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestAgentMcp(HexPointerScript test, Action<string, JsonObject?, float> ask, Func<string> answered)
    {
        string file = Path.Combine(testFolder, "agent-demo.txt");
        int requestId = 0;
        void Call(string name, JsonObject? args = null) => ask(name, args, 15);
        test.Run(() =>
        {
            File.WriteAllText(file, "let speed = 3;\nlet distance = speed * 2;\n");
            Open(file);
            code.GoTo(0, 4);
            RequestAgent("explain");
            requestId = agentConversation.Requests.Last().Id;
        });
        test.Settle(0.2f);
        test.Check("agent: Explain opens a conversation with a line snapshot", () => agentWindow is not null
            && agentConversation.Find(requestId) is { Kind: "explain", Source: "let speed = 3;", Line: 1 });
        Call("hex_agent_requests");
        test.Check("agent: MCP reads the pending user request", () => answered().Contains("let speed = 3;") && answered().Contains("explain"));
        Call("hex_agent_reply", new JsonObject { ["request_id"] = 1, ["text"] = "This sets speed to 3. The next line doubles it to compute distance." });
        test.Check("agent: replies appear in the conversation", () => agentTranscript!.Text.Contains("This sets speed to 3."));
        test.Run(() => Shot("agent-conversation"));
        test.Settle(0.2f);
        test.Run(() => { agentInput!.Text = "Why multiply by two?"; agentInput.OnSubmitted!(agentInput.Text); });
        test.Check("agent: chat can send a follow-up through the composer", () => agentConversation.Requests.Last() is { Kind: "chat", Prompt: "Why multiply by two?" });
        test.Run(() => agentWindow?.Close());
        Call("hex_agent_hint", new JsonObject { ["id"] = "speed", ["line"] = 1, ["column"] = 5, ["text"] = "Speed controls the distance calculation." });
        test.Settle(0.2f);
        test.Check("agent: hints are anchored to the requested position and have a close control", () =>
            code.AgentHints is [{ Line: 0, Column: 4 }] && code.AgentHintDismissBounds("speed").Width > 0);
        test.Run(() => Shot("agent-inline-hint"));
        test.Run(() => agentPointer.Release());
        test.Click(() => code.Module?.ToWorld(code.AgentHintDismissBounds("speed").Center) ?? Vector2.Zero);
        test.Wait();
        test.Check("agent: clicking the hint's close control dismisses it", () => code.AgentHints.Count == 0);
        test.Run(() =>
        {
            QueueAgentRequest("change", "Set speed to 5", document, 0, 0);
            requestId = agentConversation.Requests.Last().Id;
        });
        // Request ids are monotonic; this suite starts with an empty conversation: explain=1, chat=2, change=3.
        Call("hex_agent_edit", new JsonObject { ["request_id"] = 3, ["text"] = "let speed = 5;", ["message"] = "Set speed to 5." });
        test.Check("agent: a requested edit updates only its captured lines and leaves the file unsaved", () =>
            code.Document.LineAt(0) == "let speed = 5;" && code.Document.LineAt(1) == "let distance = speed * 2;"
            && File.ReadAllText(file).StartsWith("let speed = 3;") && document.Modified);
        test.Run(() => code.Document.Undo());
        test.Check("agent: one undo restores the requested line", () => code.Document.LineAt(0) == "let speed = 3;");
        test.Run(() =>
        {
            QueueAgentRequest("change", "Change this", document, 0, 0);
            code.Document.Insert("user edit");
        });
        Call("hex_agent_edit", new JsonObject { ["request_id"] = 4, ["text"] = "stale replacement" });
        test.Check("agent: edits reject stale snapshots", () => answered().Contains("document has changed") && !code.Text.Contains("stale replacement"));
        test.Run(() =>
        {
            agentWindow?.Close();
            code.Document.Undo();
            CloseNow();
            agentConversation.Clear();
            agentTargets.Clear();
        });
    }
}
