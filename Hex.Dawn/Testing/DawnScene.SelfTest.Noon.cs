using System.Numerics;

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
        test.Run(() =>
        {
            chat!.Approval("Run the tests", "dotnet test", answer => allowed = answer);
            chat.Note("[x] Read the loader\n[~] Change the cache\n[ ] Run the tests");
            noonWait = 0;
        });
        test.Settle(0.4f);
        test.Check("noon: the Inbox tab counts what waits and the Plan tab how far the agent's list got", () =>
            noonTabButtons[(int)NoonStage.Inbox].Label == TabLabel(NoonStage.Inbox, "1") && noonTabButtons[(int)NoonStage.Plan].Label == TabLabel(NoonStage.Plan, "1/3"));
        PressKeys(test, "Alt+2");
        test.Settle(0.4f);
        test.Check("noon: Alt+2 shows the Inbox, the request on a card with Allow and Deny and their keys", () =>
            noonStage == NoonStage.Inbox && noonInbox.Visible && !agentsView.Visible
            && Descendants(noonInboxRows).OfType<Button>().Any(button => button.Label == Said("noon.allow_key", ("key", ALLOW_KEY))));
        test.Run(() => Shot("noon-inbox"));
        PressKeys(test, "Ctrl+Alt+Y");
        test.Settle(0.4f);
        test.Check("noon: Ctrl+Alt+Y allows the request waiting and the Inbox empties", () =>
            allowed == true && !chat!.Pending.Any() && noonTabButtons[(int)NoonStage.Inbox].Label == TabLabel(NoonStage.Inbox, string.Empty));
        PressKeys(test, "Alt+4");
        test.Settle(0.4f);
        test.Check("noon: Alt+4 shows the plan, a line an item", () =>
            noonStage == NoonStage.Plan && Descendants(noonPlanRows).OfType<Label>().Count(label => label.Text.Contains("Change the cache", StringComparison.Ordinal)) == 1);
        test.Run(() => Shot("noon-plan"));
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
