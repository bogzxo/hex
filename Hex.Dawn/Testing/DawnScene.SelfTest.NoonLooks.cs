using Hex.Shell;

using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// Agentic in every look Hex comes with at 100, 150 and 200%. Its columns laid out, the title bar marking Noon, the
// look's Noon theme on where it has one and its own theme where it hasn't, and nothing of Noon's left on Dawn after.
// HEX_SELFTEST_SHOTS names a folder for a picture of each.
internal sealed partial class DawnScene
{
    private void TestNoonLooks(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        float scaleBefore = 1.0f;
        HexAgentChat? chat = null;

        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            chat = new HexAgentChat(++agentChatsMade, "noon_agent") { Model = "claude-opus-5-5" };
            chat.Ask("show Agentic in every look");
            chat.Say("Here it is, the fleet, the chat and Details.");
            chat.Done();
            agentChats.Add(chat);
            OpenAgentChat(chat);
        });

        foreach (HexLook look in HexLooks.BuiltIn)
        {
            foreach (float scale in (float[])[1.0f, 1.5f, 2.0f])
            {
                string named = $"{look.Name} at {scale * 100:0}%";
                test.Run(() =>
                {
                    ShowFace(HexFace.Dawn);
                    PickLook(look);
                    SetInterfaceSize(scale, remember: false);
                    ShowFace(HexFace.Noon);
                    noonWait = 0;
                });
                test.Settle(0.3f);
                test.Check($"noon looks: {named}, Agentic's columns are laid out and the title bar marks Noon", () =>
                    noonView.Visible && noonFleet.Bounds.Width > 0 && noonCenter.Bounds.Width > 0 && noonFleet.Bounds.Height > 0
                    && modeToggle.Parent!.Children.OfType<Button>().Single(button => button.Selected).Label == HexFace.Noon.Name());
                test.Check($"noon looks: {named}, the stage tabs are sized to their content, in the strip, and only the picked one is marked", () =>
                    noonTabPanels.All(tab => tab.Size.X >= TAB_MIN && tab.Bounds.Max.X <= noonCenter.Bounds.Max.X + 0.5f)
                    && noonTabPanels.Select(tab => tab.Size.X).Distinct().Count() > 1
                    && noonTabLines.Count(line => line.Visible) == 1 && noonTabLines[(int)noonStage].Visible
                    && noonTabKeys.All(key => key.Visible == noonTabKeys[0].Visible));
                // A strip too narrow for the keys drops them and the tabs still fit, then it comes back at the room it had
                test.Check($"noon looks: {named}, a narrow strip drops the tabs' keys, the tabs still fit and they come back with the room", () =>
                {
                    float room = noonTabsFitted.Room;
                    FitNoonTabs(float.MaxValue);
                    float expandedWidth = noonTabPanels.Sum(tab => tab.Size.X) + noonTabs.Spacing * (noonTabPanels.Length - 1);
                    FitNoonTabs(expandedWidth - 1);
                    bool compact = noonTabKeys.All(key => !key.Visible)
                        && noonTabPanels.Sum(tab => tab.Size.X) + noonTabs.Spacing * (noonTabPanels.Length - 1) <= expandedWidth - 1;
                    FitNoonTabs(expandedWidth);
                    bool expanded = noonTabKeys.All(key => key.Visible);
                    FitNoonTabs(room);
                    return compact && expanded;
                });
                test.Check($"noon looks: {named}, Agentic is in the look's Noon theme where it has one", () =>
                    NoonThemeOf(UISkins.Find(settings.EditorSkin), settings.EditorTheme) is not { } noon || compositor.Skin?.Theme == noon);
                string shot = $"noon-{look.Name.Replace(' ', '-').ToLowerInvariant()}-{scale * 100:0}";
                // A scale or a look changed takes a few frames to be drawn at, the first change of the run the longest.
                // Taken straight after the checks, the picture was of a frame from before it, an empty canvas
                test.Run(() => Engine.WindowManager.RequestFrames());
                test.Settle(0.5f);
                test.Run(() =>
                {
                    if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                        Engine.CaptureScreenshot(Path.Combine(shots, shot + ".png"));
                });
                test.Settle(0.1f);
                test.Run(() => ShowFace(HexFace.Dawn));
                test.Settle(0.2f);
                test.Check($"noon looks: {named}, back in Dawn nothing of Noon's theme is left", () =>
                    compositor.Skin?.Theme is not { } theme || !theme.EndsWith("_noon", StringComparison.Ordinal));
            }
        }

        test.Run(() =>
        {
            SetInterfaceSize(scaleBefore, remember: false);
            PickLook(HexLooks.Solstice);
            UseEditorSkin(skinBefore, themeBefore, save: false);
            if (chat is not null)
                agentChats.Remove(chat);
            agentChat = null;
        });
        test.Settle(0.3f);
    }
}
