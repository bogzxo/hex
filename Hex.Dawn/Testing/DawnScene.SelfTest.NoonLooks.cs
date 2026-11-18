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
                    SetScale(scale, remember: false);
                    ShowFace(HexFace.Noon);
                    noonWait = 0;
                });
                test.Settle(0.3f);
                test.Check($"noon looks: {named}, Agentic's columns are laid out and the title bar marks Noon", () =>
                    noonView.Visible && noonFleet.Bounds.Width > 0 && noonCenter.Bounds.Width > 0 && noonFleet.Bounds.Height > 0
                    && modeToggle.Parent!.Children.OfType<Button>().Single(button => button.Selected).Label == HexFace.Noon.Name());
                test.Check($"noon looks: {named}, Agentic is in the look's Noon theme where it has one", () =>
                    NoonThemeOf(UISkins.Find(settings.EditorSkin), settings.EditorTheme) is not { } noon || compositor.Skin?.Theme == noon);
                string shot = $"noon-{look.Name.Replace(' ', '-').ToLowerInvariant()}-{scale * 100:0}";
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
            SetScale(scaleBefore, remember: false);
            PickLook(HexLooks.Solstice);
            UseEditorSkin(skinBefore, themeBefore, save: false);
            if (chat is not null)
                agentChats.Remove(chat);
            agentChat = null;
        });
        test.Settle(0.3f);
    }
}
