using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test, how the settings look in every look Hex comes with and at 100, 150 and 200%. The window as it opens, a
// search with what it found lit, a changed setting under Modified with its reset, the looks to pick from, the keyboard's
// shortcuts and the Language Servers window's settings. It checks the rows stay inside the window and their controls in
// one column at every size, and with HEX_SELFTEST_SHOTS set it leaves a picture of each.
internal sealed partial class DawnScene
{
    private void TestSettingsLook(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        float sizeBefore = 1.0f;

        void ShotNow(string name)
        {
            test.Settle(0.4f);
            test.Run(() =>
            {
                if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                    Engine.CaptureScreenshot(Path.Combine(shots, "settings-look-" + name + ".png"));
            });
            test.Settle(0.5f);
        }

        test.Run(() =>
        {
            sizeBefore = compositor.Scale;
            CloseAll();
        });
        test.Settle(0.3f);

        (HexLook Look, float[] Sizes)[] runs =
        [
            (HexLooks.Solstice, [1.0f, 1.5f, 2.0f]),
            (HexLooks.SolsticeDay, [1.0f, 1.5f, 2.0f]),
            (HexLooks.VisualStudio, [1.0f, 1.5f, 2.0f]),
            (HexLooks.Graphite, [1.0f]),
            (HexLooks.Fluid, [1.0f]),
            (HexLooks.Frosted, [1.0f]),
            (HexLooks.Flat, [1.0f]),
        ];

        foreach (var (look, sizes) in runs)
        {
            string lookName = look.Name.ToLowerInvariant().Replace(' ', '-');
            test.Run(() => PickLook(look));
            test.Settle(1.5f);

            foreach (float size in sizes)
            {
                string tag = $"{lookName}-{(int)(size * 100)}";
                test.Run(() => SetInterfaceSize(size, remember: false));
                test.Settle(0.4f);

                // The window as it opens, at the top
                test.Run(() =>
                {
                    CloseSettings();
                    OpenSettings(null);
                    SettingsPage?.Find(string.Empty);
                });
                test.Settle(0.6f);
                ShotNow(tag + "-home");
                test.Check($"settings look: {tag} the window is on the screen with its rows", () =>
                    SettingsWindow is { } window && SettingsPage is { } page && page.RowsShown > 0
                    && window.Bounds.Max.X <= chrome.Frame.Max.X + 1.0f && window.Bounds.Max.Y <= chrome.Frame.Max.Y + 1.0f);

                // A search, what it found lit in the rows and the sections
                test.Run(() => SettingsPage?.Find("tab"));
                test.Settle(0.4f);
                ShotNow(tag + "-search");

                // A setting changed, its mark and reset, and the Modified chip with it
                test.Run(() =>
                {
                    SettingsPage?.Find(string.Empty);
                    SettingsPage?.Enter("tab_size", new NumberValue(2));
                    SettingsPage?.Reveal("tab_size");
                    compositor.Focus = null;
                });
                ShotNow(tag + "-modified");
                // Two columns where the window has the room, the controls lined up down the right, one under the sentence
                // where it hasn't, inside the window either way
                test.Check($"settings look: {tag} the controls are inside the window, lined up down the right when they're beside the sentence", () =>
                    SettingsPage is { } page && SettingsWindow is { } window
                    && page.RowParts("tab_size") is { } tab && page.RowParts("insert_spaces") is { } spaces
                    && tab.Control.Max.X <= window.Bounds.Max.X && spaces.Control.Max.X <= window.Bounds.Max.X
                    && (tab.Control.Min.X <= tab.Help.Max.X || Math.Abs(tab.Control.Max.X - spaces.Control.Max.X) < 1.5f));
                test.Check($"settings look: {tag} a row says what it does in a sentence, the rest when the pointer rests on it", () =>
                    SettingsPage?.HelpShown("tab_size") is { } help && help.EndsWith("1 to 16.", StringComparison.Ordinal) && !help.Contains(".editorconfig", StringComparison.Ordinal));
                test.Check($"settings look: {tag} a changed row has its mark and its reset", () =>
                    SettingsPage is { } page && page.RowMarked("tab_size") == true && page.RowBounds("tab_size") is { Reset.IsEmpty: false });
                test.Run(() => SettingsPage?.Reset("tab_size"));

                // How Hex looks, the looks to pick from
                test.Run(() => SettingsPage?.GoTo(EditorSettingsCatalog.PROFILES));
                ShotNow(tag + "-looks");
                test.Check($"settings look: {tag} going to a section in a folded part opens it down the left", () =>
                    !SettingsPage.IsPartCollapsed(SettingsPage.PartOf(EditorSettingsCatalog.PROFILES)) && SettingsPage?.CurrentGroup == EditorSettingsCatalog.PROFILES);

                // A search, then the rest of a row's help when a word is only there
                test.Run(() => SettingsPage?.Find("editorconfig indentation"));
                test.Settle(0.3f);
                test.Check($"settings look: {tag} a word found only past a row's first sentence shows all of it", () =>
                    SettingsPage?.HelpShown("tab_size") is { } help && help.Contains("editorconfig", StringComparison.Ordinal));
                test.Run(() => SettingsPage?.Find(string.Empty));

                // The keyboard's shortcuts, a window of their own
                test.Run(() =>
                {
                    CloseSettings();
                    OpenKeysWindow();
                });
                ShotNow(tag + "-keys");
                test.Run(() => keysWindow?.Close());

                // The Language Servers window's settings of a language
                test.Run(() => OpenLanguageServers("csharp", settings: true));
                test.Settle(0.6f);
                ShotNow(tag + "-servers");
                test.Check($"settings look: {tag} the Language Servers window is on the screen", () =>
                    serversWindow is { } window && window.Bounds.Max.X <= chrome.Frame.Max.X + 1.0f && window.Bounds.Max.Y <= chrome.Frame.Max.Y + 1.0f
                    && window.Bounds.Min.X >= chrome.Frame.Min.X - 1.0f && window.Bounds.Min.Y >= chrome.Frame.Min.Y - 1.0f);
                test.Check($"settings look: {tag} a server's setting is a row by its name, its key small after it, what it costs said", () =>
                    serversPage is { } shown && ServerWords(shown) is var words
                    && words.Any(text => text.Contains("Analyzers enabled", StringComparison.Ordinal))
                    && words.Any(text => text.Contains("csharp.analyzersEnabled", StringComparison.Ordinal))
                    && words.Any(text => text.Contains(Said("server_settings.cost"), StringComparison.Ordinal)));
                test.Run(() => serversWindow?.Close());
                test.Settle(0.3f);
            }
        }

        test.Run(() =>
        {
            CloseSettings();
            SetInterfaceSize(sizeBefore, remember: false);
            settings.EditorSkin = skinBefore;
            UseEditorTheme(themeBefore, save: false);
        });
        test.Settle(0.5f);
    }

    // Every label's text under a part of a window, for a check of what it says
    private static List<string> ServerWords(UIComponent under)
    {
        var words = new List<string>();
        void Walk(UIComponent at)
        {
            if (at is Label label)
                words.Add(label.Text);
            foreach (UIComponent child in at.Children)
                Walk(child);
        }

        Walk(under);
        return words;
    }
}
