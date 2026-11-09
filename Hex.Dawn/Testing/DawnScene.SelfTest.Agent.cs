using System.Numerics;
using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestAgentMcp(HexPointerScript test, Action<string, JsonObject?, float> ask)
    {
        string file = Path.Combine(testFolder, "agent-demo.txt");
        int runs = 0;
        void Call(string name, JsonObject? args = null) => ask(name, args, 15);
        test.Run(() =>
        {
            File.WriteAllText(file, "let speed = 3;\nlet distance = speed * 2;\n");
            Open(file);
            NewAgentChat();
            code.Document.Select(new UITextPosition(0, 4), new UITextPosition(0, 9));
            runs = agentRuns.Count;
            RequestAgent("explain");
        });
        test.Settle(0.2f);
        test.Check("agent: Explain prepares an editable draft with the exact selection without invoking an agent", () =>
            Side == HexSide.Agents && agentsInput.Text == Said("agent.explain_prompt")
            && agentDraftContext is { Source: "speed", Line: 1, Column: 5, EndLine: 1, EndColumn: 10 }
            && agentRuns.Count == runs && agentChat is null);
        test.Run(() =>
        {
            code.GoTo(1, 0);
            agentsInput.Text = "Explain the name";
        });
        test.Check("agent: captured attachment survives caret movement and instruction edits", () =>
            AgentDraftPrompt(agentsInput.Text).Contains("\nspeed\n\n---\n")
            && AgentDraftPrompt(agentsInput.Text).EndsWith("Explain the name")
            && AgentDraftCaption()!.Contains("1:5–1:10"));
        test.Run(() => Shot("agent-draft"));
        test.Run(() => RequestAgent("explain"));
        test.Check("agent: repeated draft actions preserve a custom instruction without duplication", () => agentsInput.Text == "Explain the name");
        test.Run(() =>
        {
            agentDraftContext = new(file, 1, 1, 1, 2, 1, "source\n\n---\nwith delimiter");
        });
        test.Check("agent: snapshot preserves source containing a display separator", () =>
            AgentDraftPrompt("Only this instruction").Contains("source\n\n---\nwith delimiter")
            && AgentDraftPrompt("Only this instruction").EndsWith("Only this instruction"));
        test.Check("agent: typed user metadata keeps display instruction separate from the exact retry payload", () =>
        {
            string payload = AgentDraftPrompt("Only this instruction");
            var chat = new HexAgentChat(-1, "test");
            var entry = chat.Ask(payload, "Only this instruction", "Only this instruction", file + ":1:1–2:1");
            return entry.Text == payload && entry.AskedText == "Only this instruction" && entry.ContextLocation == file + ":1:1–2:1";
        });
        test.Run(() => { agentsInput.Text = ""; agentsInput.OnChanged!(""); });
        test.Check("agent: erasing an instruction removes its captured context", () => agentDraftContext is null);
        test.Run(() => NewAgentChat());
        test.Check("agent: starting a new chat clears the captured draft", () => agentDraftContext is null && agentsInput.Text.Length == 0);
        test.Run(() => { code.GoTo(0, 0); RequestAgent("change"); });
        test.Check("agent: Change prepares an instruction with the current line and never invokes an agent", () =>
            agentDraftContext is { Source: "let speed = 3;", Column: 1 }
            && agentsInput.Text == Said("agent.change_prompt") && agentRuns.Count == runs);
        test.Run(() => NewAgentChat());
        Call("hex_agent_hint", new JsonObject { ["id"] = "speed", ["line"] = 1, ["column"] = 5, ["text"] = "Speed controls the distance calculation." });
        test.Settle(0.2f);
        test.Check("agent: hints are anchored to the requested position and have a close control", () =>
            code.AgentHints is [{ Line: 0, Column: 4 }] && code.AgentHintDismissBounds("speed").Width > 0);
        test.Run(() => Shot("agent-inline-hint"));
        test.Run(() => agentPointer.Release());
        test.Click(() => code.Module?.ToWorld(code.AgentHintDismissBounds("speed").Center) ?? Vector2.Zero);
        test.Wait();
        test.Check("agent: clicking the hint close control dismisses it", () => code.AgentHints.Count == 0);
        Call("hex_agent_hint", new JsonObject { ["id"] = "speed", ["line"] = 1, ["column"] = 5, ["text"] = "Temporary hint" });
        test.Run(() => code.Document.Insert("user edit"));
        test.Settle(0.2f);
        test.Check("agent: editing a document clears snapshot hints", () => code.AgentHints.Count == 0);
        test.Run(() =>
        {
            code.Document.Undo();
            NewAgentChat();
            ShowSide(HexSide.Explorer);
            CloseNow();
        });
    }
}
