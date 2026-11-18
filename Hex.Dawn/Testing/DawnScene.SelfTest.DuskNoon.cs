using System.Text.Json.Nodes;

using Hex.Shell;

namespace Hex.Dawn;

// Agentic and Dusk in the one window. With Dusk on screen the agents go on, a message between two of them delivered
// and the chats moving without the editor coming forward, and the Noon button of Dusk's title bar (the face it asks for
// and the swap it makes) brings the editor back showing Agentic. Only when the self test was asked for this suite, the
// one that has both modes in its window.
internal sealed partial class DawnScene
{
    private void TestDuskNoon(HexPointerScript test)
    {
        HexAgentChat? lead = null, scout = null;
        Task<McpToolResult>? sent = null;
        long versionBefore = 0;

        test.Check("dusk-noon: Dusk shares the self test's window", () => HexModes.Together);
        test.Run(() =>
        {
            lead = new HexAgentChat(++agentChatsMade, "noon_agent");
            lead.Ask("keep working while Dusk is up");
            scout = new HexAgentChat(++agentChatsMade, "noon_agent") { Parent = lead.Id, Depth = 1, Name = "Scout" };
            scout.Ask("look round");
            scout.Done();
            agentChats.Add(lead);
            agentChats.Add(scout);
            ShowFace(HexFace.Dawn);
            HexModes.Swap();
        });
        test.WaitFor(() => HexModes.Current == HexMode.Dusk, 10.0f);
        test.Settle(0.5f);
        test.Check("dusk-noon: Dusk is on screen", () => HexModes.Current == HexMode.Dusk);

        // Behind Dusk, the scout's message to the one that started it goes through and the chats move on
        test.Run(() =>
        {
            versionBefore = hub.Version;
            lead!.Say("still going behind the terminal");
            sent = McpTools.CallAs("hex_agent_message", new JsonObject { ["text"] = "found it while Dusk was up", ["kind"] = "finding" }, CancellationToken.None, scout!.Id);
        });
        test.WaitFor(() => sent?.IsCompleted == true, 10.0f);
        test.Settle(0.3f);
        test.Check("dusk-noon: with Dusk on screen a message between agents is delivered and the chats move on", () =>
            HexModes.Current == HexMode.Dusk && sent is { IsCompletedSuccessfully: true, Result.IsError: false }
            && hub.MailOf(lead!).All.Any(entry => entry.Mail.Text == "found it while Dusk was up") && hub.Version > versionBefore);

        // As Dusk's Noon button does, the face asked for and the swap made
        test.Run(() =>
        {
            HexModes.FaceWanted = HexFace.Noon;
            HexModes.Swap();
        });
        test.WaitFor(() => HexModes.Current == HexMode.Dawn && Face == HexFace.Noon, 10.0f);
        test.Settle(0.4f);
        test.Check("dusk-noon: Dusk's Noon button brings the editor back showing Agentic", () =>
            HexModes.Current == HexMode.Dawn && Face == HexFace.Noon && noonView.Visible && HexModes.FaceWanted is null);

        test.Run(() =>
        {
            lead!.Done();
            CloseAgentChat(lead);
            ShowFace(HexFace.Dawn);
        });
        test.Settle(0.2f);
    }
}
