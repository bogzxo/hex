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
