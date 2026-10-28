using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of Dusk's settings having what Dawn's have, Editor · Text's tab size and the rest in the one window,
// found by the search and written into the editor's section for Dawn to take in, the editor's other keys kept beside
internal sealed partial class DuskScene
{
    private void TestSettingsText(DuskScript test)
    {
        test.Run(() =>
        {
            SettingsPage.ForgetFolding();
            ShowSettings();
        });
        test.Wait();
        test.Wait();
        test.Check("settings: Dusk's window opens on the terminal with the other parts folded down the left", () =>
            SettingsPage is { } page && !SettingsPage.IsPartCollapsed("Terminal") && SettingsPage.IsPartCollapsed("Editor")
            && page.CurrentGroup is { } current && current.StartsWith("Terminal · ", StringComparison.Ordinal));
        test.Run(() => SettingsPage!.SetPartCollapsed("Editor", false));
        test.Check("settings: Dusk's window has Editor · Text, the tab size and spaces for tabs, as Dawn's does", () =>
            SettingsPage is { } page && page.Settings.Any(setting => setting.Key == "tab_size" && setting.Group == EditorSettingsCatalog.TEXT)
            && page.Settings.Any(setting => setting.Key == "insert_spaces" && setting.Group == EditorSettingsCatalog.TEXT)
            && page.SectionsShown.Contains(EditorSettingsCatalog.TEXT));

        test.Run(() => SettingsPage!.Find("tab size"));
        test.Wait();
        test.Check("settings: searching tab size finds it", () => SettingsPage!.IsRowShown("tab_size"));
        test.Run(() => SettingsPage!.Find(string.Empty));

        // Both the editor's sources write the one section, each keeps what the other wrote
        test.Run(() =>
        {
            SettingsPage!.Enter("wrap_code", Values.Bool(true));
            SettingsPage.Enter("tab_size", new NumberValue(2.0f));
        });
        test.Wait();
        test.Check("settings: setting the tab size writes editor.tab_size into the file and keeps the editor's other keys", () =>
            host.Settings.Path is null || SettingsSections.Read(host.Settings.Path) is { } all
                && SettingsSections.Section(all, SettingsSections.EDITOR) is { } editor
                && editor.GetValueOrDefault("tab_size") is NumberValue { Value: 2 }
                && editor.GetValueOrDefault("wrap_code") is BooleanValue { Value: true });
        test.Check("settings: and the row says it's changed", () => SettingsPage!.IsModified!("tab_size"));
        float scrollSpeed = compositor.ScrollSpeed;
        test.Run(() => SettingsPage!.Enter("scroll_speed", new NumberValue(2)));
        test.Check("settings: universal scroll speed is shared and live in Dusk", () =>
            SettingsPage!.Settings.Any(setting => setting.Key == "scroll_speed") && (host.Settings.Path is null || compositor.ScrollSpeed == 2));
        test.Run(() => SettingsPage!.Enter("scroll_speed", new NumberValue(scrollSpeed)));
        test.Run(() =>
        {
            SettingsPage!.Reset("tab_size");
            SettingsPage.Reset("wrap_code");
        });
        test.Wait();
        test.Check("settings: reset, it's gone from the file again", () =>
            host.Settings.Path is null || SettingsSections.Read(host.Settings.Path) is not { } all
                || !SettingsSections.Section(all, SettingsSections.EDITOR).ContainsKey("tab_size"));
        test.Run(CloseSettings);
        test.Wait();
    }
}
