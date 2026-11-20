using System.Numerics;
using System.Text.Json.Nodes;

using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Agentic, the Noon face. The title bar's button brings it up over the editor's body with the Agents view in its middle,
// the Solstice look puts it in its own teal and Dawn's orange comes back as it's left, its key goes back and forth, the
// side bar is as wide as it was after, and a chat started in the panel is the one in Agentic's fleet and middle. The
// welcome page's Agents row offers a new agent and the chat to carry on with. HEX_SELFTEST_SHOTS names a folder for
// pictures of it.
internal sealed partial class DawnScene
{
    // A tab says its name, and its count on the badge after it, none for an empty count
    private bool TabShows(NoonStage stage, string count) =>
        noonTabButtons[(int)stage].Label == TabLabel(stage)
        && (count.Length == 0 ? !noonTabCounts[(int)stage].Visible : noonTabCounts[(int)stage] is { Visible: true } badge && badge.Text == count);

    private void TestNoon(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        HexAgentChat? chat = null;
        float sideBefore = 0;
        Button? noonButton = null;

        void Shot(string name)
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name + ".png"));
        }

        test.Run(() =>
        {
            PickLook(HexLooks.Solstice);
            ShowFace(HexFace.Dawn);
            chat = new HexAgentChat(++agentChatsMade, "noon_agent") { Model = "claude-opus-5-5" };
            chat.Ask("look round the noon face");
            chat.Say("Agentic has the fleet on the left, the chat in the middle and Details on the right.");
            chat.Done();
            agentChats.Add(chat);
            OpenAgentChat(chat);
            sideBefore = left.Size.X;
            noonButton = modeToggle.Parent!.Children.OfType<Button>().First(button => button.Label == HexFace.Noon.Name());
        });
        test.Settle(0.3f);

        test.Click(() => chrome.ToWorld(noonButton!.Bounds.Center));
        test.Settle(0.5f);
        test.Check("noon: the title bar's Noon button brings Agentic up over the editor's body, the Agents view in its middle", () =>
            Face == HexFace.Noon && noonView.Visible && !body.Visible && agentsView.Parent == noonCenter && agentsView.Visible);
        test.Check("noon: the title bar marks Noon as the face showing", () => noonButton is { Selected: true });
        test.Check("noon: in Solstice Agentic wears its teal, the settings keeping Dawn's theme", () =>
            settings.EditorTheme == "solstice" && compositor.Skin is { Theme: "solstice_noon" } skin
            && skin.Palette.Accent.Y > skin.Palette.Accent.X && skin.Palette.Accent.Z > skin.Palette.Accent.X && skin.Palette.Warning != skin.Palette.Accent);
        test.Check("noon: Agentic is still drawn flush in its theme", () => compositor.Skin is { } skin && HexLooks.Flush(skin));
        test.Run(() => noonWait = 0);
        test.Settle(0.4f);
        test.Check("noon: a chat started in the panel is the one in the fleet and in the middle", () =>
            agentChat == chat && Descendants(noonFleetRows).OfType<Label>().Any(label => label.Text.Contains("look round the noon face", StringComparison.Ordinal))
            && Descendants(agentsView).OfType<Label>().Any(label => label.Text.Contains("Details on the right", StringComparison.Ordinal)));
        test.Check("noon: the columns side by side fill Agentic's width and its height", () =>
            noonFleet.Bounds.Width > 0 && noonCenter.Bounds.Width > 0 && noonView.Bounds.Height > 100
            && MathF.Abs(noonFleet.Bounds.Width + noonCenter.Bounds.Width + (noonContext.Visible ? noonContext.Bounds.Width : 0) - noonView.Bounds.Width) < 2.0f
            && MathF.Abs(noonCenter.Bounds.Height - noonView.Bounds.Height) < 2.0f);
        test.Run(() => Shot("noon-solstice"));
        test.Settle(0.5f);

        // The server off, Agentic coming up says once a session what switching it on saves
        int hintsBefore = 0;
        bool quietBefore = settings.McpHintQuiet;
        test.Run(() =>
        {
            hintsBefore = McpHintsSaid;
            settings.McpHintQuiet = false;
            SayMcpHint = true;
            ForgetMcpHintSaid();
            ShowFace(HexFace.Dawn);
            ShowFace(HexFace.Noon);
        });
        test.Settle(0.3f);
        test.Check("noon: with the MCP server off Agentic's card says what it saves, with the way to its setting and Don't Show Again", () =>
            McpWanted || (McpHintsSaid == hintsBefore + 1
            && toasts.Cards.Any(card => card.Text == Said("mcp.hint") && card.Actions.Count == 2
                && card.Actions.Any(action => action.Label == Said("mcp.hint_open"))
                && card.Actions.Any(action => action.Label == Said("code_server.dont_show_again")))));
        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            ShowFace(HexFace.Noon);
        });
        test.Check("noon: and says it once a session, not every time Agentic comes up", () => McpWanted || McpHintsSaid == hintsBefore + 1);
        test.Run(() =>
        {
            settings.McpHintQuiet = true;
            ForgetMcpHintSaid();
            ShowFace(HexFace.Dawn);
            ShowFace(HexFace.Noon);
        });
        test.Check("noon: nor at all once Don't Show Again was pressed", () => McpWanted || McpHintsSaid == hintsBefore + 1);
        test.Run(() =>
        {
            settings.McpHintQuiet = quietBefore;
            SayMcpHint = null;
        });
        test.Settle(0.3f);

        // Agentic's horizon is the agents', running while one works and red a moment when one fails
        test.Check("noon: the horizon is empty while no agent works", () => HorizonLine is { Share: 0.0f });
        test.Run(() => chat!.Ask("work a while"));
        test.Settle(0.4f);
        test.Check("noon: an agent working runs Agentic's horizon", () => horizonAgentsWorking == 1 && HorizonLine is { Share: > 0.0f });
        test.Run(() =>
        {
            chat!.Error("the agent went wrong");
            chat.Done();
        });
        test.Settle(0.4f);
        test.Check("noon: an agent that failed turns Agentic's horizon the error's red", () =>
            HorizonLine is { Share: 1.0f } line && compositor.Skin is { } skin && line.FillColour == skin.Palette.Error);
        test.Run(() => horizonAgentFailedUntil = 0);
        test.Settle(0.2f);

        // Its key takes it back to Dawn, and the orange with it
        PressKeys(test, "Ctrl+Alt+Shift+N");
        test.Settle(0.4f);
        test.Check("noon: its key goes back to Dawn, the body and the side bar's Agents view where they were", () =>
            Face == HexFace.Dawn && !noonView.Visible && body.Visible && agentsView.Parent == left);
        test.Check("noon: leaving Agentic puts Dawn's orange back", () =>
            compositor.Skin is { Theme: "solstice" } skin && skin.Palette.Accent.X > skin.Palette.Accent.Z);
        test.Check("noon: the side bar is as wide as it was before Agentic", () => MathF.Abs(left.Size.X - sideBefore) < 1.0f);
        PressKeys(test, "Ctrl+Alt+Shift+N");
        test.Settle(0.4f);
        test.Check("noon: and the key brings Agentic up again", () => Face == HexFace.Noon && noonView.Visible);

        // The tabs over the middle, a request waiting in the Inbox and answered by its key from anywhere
        bool? allowed = null;
        float planWidthBefore = 0;
        test.Run(() =>
        {
            planWidthBefore = noonTabPanels[(int)NoonStage.Plan].Size.X;
            chat!.Approval("Run the tests", "dotnet test", answer => allowed = answer);
            chat.Note("[x] Read the loader\n[~] Change the cache\n[ ] Run the tests");
            foreach (string tool in new[] { "hex_read", "hex_search", "hex_edit", "hex_terminal_run" })
            {
                chat.Tool(tool, "mcp__hex__" + tool, "{}");
                chat.ToolDone(tool, true, "Done");
            }
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon: the Inbox tab counts what waits and the Plan tab how far the agent's list got", () =>
            TabShows(NoonStage.Inbox, "1") && TabShows(NoonStage.Plan, "1/3"));
        test.Check("noon: a count widens its tab to fit it and the tabs still fit the strip", () =>
            noonTabPanels[(int)NoonStage.Plan].Size.X > planWidthBefore
            && noonTabPanels.Sum(tab => tab.Size.X) + noonTabs.Spacing * 3 <= noonTabsFitted.Room);
        PressKeys(test, "Alt+2");
        test.Settle(0.4f);
        test.Check("noon: Alt+2 shows the Inbox, the request on a card with Allow and Deny and their keys", () =>
            noonStage == NoonStage.Inbox && noonInbox.Visible && !agentsView.Visible
            && Descendants(noonInboxRows).OfType<Button>().Any(button => button.Label == Said("noon.allow_key", ("key", ALLOW_KEY))));
        test.Run(() => Shot("noon-inbox"));
        PressKeys(test, "Ctrl+Alt+Y");
        test.Settle(0.4f);
        test.Check("noon: Ctrl+Alt+Y allows the request waiting and the Inbox empties", () =>
            allowed == true && !chat!.Pending.Any() && TabShows(NoonStage.Inbox, string.Empty));
        PressKeys(test, "Alt+4");
        test.Settle(0.4f);
        test.Check("noon: Alt+4 shows the plan, a line an item", () =>
            noonStage == NoonStage.Plan && Descendants(noonPlanRows).OfType<Label>().Count(label => label.Text.Contains("Change the cache", StringComparison.Ordinal)) == 1);
        test.Run(() => Shot("noon-plan"));

        McpServer? planServer = null;
        Task<JsonNode?>? planReply = null;
        test.Run(() =>
        {
            _ = McpTools;
            planServer = MakeMcpServer(chat!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        void PlanCall(string name, string args)
        {
            test.Run(() => planReply = planServer!.Handle(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(args) }
            }));
            test.WaitFor(() => planReply?.IsCompleted == true, 5);
        }

        string PlanReply() => planReply?.Result?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;

        PlanCall("hex_plan_update", """{"merge":false,"title":"Fix links","items":[{"id":"read","text":"Find the handler","state":"doing","note":"From chat"},{"text":"Test the link"}]}""");
        test.Settle(0.5f);
        test.Check("noon: a tagged MCP plan replaces the legacy checklist and redraws the open tab", () =>
            PlanReply().StartsWith("Plan: 0 of 2 done.", StringComparison.Ordinal)
            && TabShows(NoonStage.Plan, "0/2")
            && Descendants(noonPlanRows).OfType<Label>().Any(label => label.Text.Contains("Fix links", StringComparison.Ordinal))
            && Descendants(noonPlanRows).OfType<Label>().Any(label => label.Text.Contains("From chat", StringComparison.Ordinal)));

        PlanCall("hex_plan_update", """{"items":[{"id":"read","state":"done"},{"text":"Test the link","state":"blocked"}]}""");
        PlanCall("hex_plan_read", "{}");
        test.Check("noon: the plan tool merges by ID and text, preserving fields and round-tripping with the chat", () =>
        {
            HexAgentPlan? restored = HexAgentChatStore.Read(JsonNode.Parse(HexAgentChatStore.Write(chat!).ToJsonString()))?.Plan;
            return JsonNode.Parse(PlanReply()) is JsonObject saved && JsonNode.DeepEquals(restored?.ToJson(), saved)
                && restored is { Title: "Fix links", Done: 1, Items.Count: 2 }
                && restored.Items[0] is { Id: "read", Text: "Find the handler", Note: "From chat" }
                && restored.Items[1].State == HexPlanState.Blocked;
        });

        PlanCall("hex_plan_update", """{"items":[{"id":"read","state":"doing"},{"text":"Broken","state":"unknown"}]}""");
        test.Check("noon: an invalid item leaves the plan unchanged",
            () => planReply?.Result?["result"]?["isError"]?.GetValue<bool>() == true && chat!.Plan?.Done == 1);

        NoonPlanPatches(test, () => chat!, PlanCall, PlanReply);
        NoonPlanCoordination(test, () => chat!);

        test.Check("noon: the tools summary gives reads, searches, edits and terminal calls distinct colours", () =>
        {
            if (Skin is not { } skin)
                return false;
            string[] toolNames = ["hex_read", "hex_search", "hex_edit", "hex_terminal_run"];
            string[] colours = [.. toolNames.Select(tool => HexColour(HexAgentRows.ToolColour(tool, skin.Palette)!.Value))];
            return colours.Distinct().Count() == 4 && toolNames.Zip(colours).All(item =>
                Descendants(noonContextRows).OfType<Label>().Any(label =>
                    label.Text.Contains($"[color:{item.Second}]", StringComparison.Ordinal)
                    && label.Text.Contains(item.First, StringComparison.Ordinal)));
        });
        SemanticShot(test, "hex-plan-and-tool-colours.png");

        PlanCall("hex_plan_update", """{"merge":false,"items":[]}""");
        test.Settle(0.5f);
        test.Check("noon: an explicit empty plan clears the tab without restoring the old checklist", () =>
            TabShows(NoonStage.Plan, string.Empty)
            && Descendants(noonPlanRows).OfType<Label>().Any(label => label.Text == Said("noon.plan_empty")));

        PressKeys(test, "Alt+3");
        test.Settle(0.4f);
        test.Check("noon: Alt+3 shows the changes, none yet for a chat that wrote nothing", () =>
            noonStage == NoonStage.Changes && Descendants(noonChangesRows).OfType<Label>().Any(label => label.Text == Said("noon.changes_empty")));
        PressKeys(test, "Alt+1");
        test.Settle(0.3f);
        test.Check("noon: Alt+1 is the chat again", () => noonStage == NoonStage.Chat && agentsView.Visible && !noonInbox.Visible);

        // An @ in the box lists the files that fit and Tab puts the first one's path in
        string? mentioned = null;
        test.Run(() =>
        {
            // A folder and an index of its own, the self-test's Hex has no workspace for the harness to look in
            string folder = Path.Combine(Path.GetTempPath(), "hex-mention-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "MentionTarget.cs"), "class MentionTarget { }\n");
            mentionIndex = new HexCodeIndex(() => [folder]) { StaleAfter = 0 };
            mentionIndex.Look(force: true);
            mentioned = HexCodeIndex.FindFiles(mentionIndex.Latest, "MentionTarget").Select(each => each.File.Relative.Replace('\\', '/')).FirstOrDefault();
            agentsInput.Focus();
            agentsInput.Text = "see @MentionTar";
            agentsInput.Document.MoveTo(new UITextPosition(0, agentsInput.Text.Length));
        });
        test.Settle(0.3f);
        test.Check("noon: an @ in the box lists the files that fit what's typed after it", () =>
            mentioned is not null && agentsMention is not null && Descendants(agentsMention).OfType<Button>().Any(button => button.Label.Contains("MentionTarget.cs", StringComparison.Ordinal)));
        test.Run(() => TakeFirstMention());
        test.Settle(0.2f);
        test.Check("noon: and Tab puts the first one's path in with a space after it", () => agentsInput.Text == $"see @{mentioned} " && agentsMention is null);
        test.Run(() =>
        {
            agentsInput.Text = string.Empty;
            mentionIndex = null;
        });

        // The fleet put away for the room and brought back
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.3f);
        test.Check("noon: Ctrl+Alt+B puts the fleet away, the middle taking its room", () => !noonFleet.Visible && noonFleetBack.Visible);
        PressKeys(test, "Ctrl+Alt+B");
        test.Settle(0.3f);
        test.Check("noon: and brings it back", () => noonFleet.Visible && !noonFleetBack.Visible);
        // The chat is made by a step that hasn't run yet as the steps are put down, so it's asked for as they run
        TestNoonNarrowFleet(test, () => chat!);

        // Solstice Day has its own Noon, a deeper teal on the paper
        test.Run(() => PickLook(HexLooks.SolsticeDay));
        test.Settle(0.4f);
        test.Check("noon: Solstice Day picked in Agentic puts it in Solstice Day's Noon", () =>
            settings.EditorTheme == "solstice_day" && compositor.Skin is { Theme: "solstice_day_noon" } skin && skin.Palette.LightField);
        test.Run(() => Shot("noon-solstice-day"));
        test.Settle(0.5f);

        // A look with one accent for every face is left as it is
        test.Run(() => PickLook(HexLooks.Graphite));
        test.Settle(0.4f);
        test.Check("noon: a look without a Noon theme keeps its own in Agentic", () => compositor.Skin?.Theme == settings.EditorTheme);
        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            PickLook(HexLooks.Solstice);
        });
        test.Settle(0.3f);

        // The welcome page's Agents row, over the empty layout Hex started with
        test.Run(() =>
        {
            welcomeWanted = true;
            Show(NewDocument());
            CloseAll();
        });
        test.Settle(0.4f);
        Button[] AgentsRow() => (welcome is { Visible: true } ? welcome : plainPage) is { Visible: true } page
            ? [.. welcomeSizes.SelectMany(sizing => sizing.Agents.Children.OfType<Button>()).Where(button => Descendants(page).Contains(button))]
            : [];
        test.Check("noon: the welcome page has an Agents row with a new agent and the chat to carry on with", () =>
            AgentsRow() is { Length: >= 2 } row && row[0].Label == Said("welcome.new_agent")
            && row[1].Label.Contains("look round the noon face", StringComparison.Ordinal));
        test.Run(() => Shot("noon-welcome"));
        test.Settle(0.5f);
        test.Run(() => AgentsRow()[1].OnPressed!());
        test.Settle(0.4f);
        test.Check("noon: Continue opens that chat in Agentic", () => Face == HexFace.Noon && agentChat == chat);

        test.Run(() =>
        {
            ShowFace(HexFace.Dawn);
            welcomeWanted = false;
            if (chat is not null)
                agentChats.Remove(chat);
            agentChat = null;
            UseEditorSkin(skinBefore, themeBefore, save: false);
        });
        test.Settle(0.3f);
    }
}
