using Horizon.UI.Components;

using Hex.Shell;

namespace Hex.Dawn;

// The self test of what the settings say and where. The editor first with its text at the top, git and running a file
// in sections of their own, the code's zoom a slider, Color Theme showing each theme as the list goes past it and
// putting the old one back on Escape, and the settings file opening in a tab. Editor, Text has a test of its own.
internal sealed partial class DawnScene
{
    private void TestSettingsLooks(HexPointerScript test)
    {
        /* The sections of the editor */

        string? Group(string key) => SettingsPage?.Settings.FirstOrDefault(setting => setting.Key == key)?.Group;
        test.Run(() => OpenSettings());
        test.Wait();
        test.Check("settings looks: the editor comes first, then the terminal, how Hex looks and the extensions", () =>
            SettingsPage!.Settings.Select(setting => setting.Group.Split(" · ")[0]).Distinct().SequenceEqual(["Editor", "Terminal", "Appearance", "Extensions"])
            && SettingsPage.Settings.Where(setting => setting.Group == EditorSettingsCatalog.THEME).Select(setting => setting.Key).SequenceEqual(["editor_skin", "theme"]));
        test.Check("settings looks: the sections go Editor · Text first, then Code, Files, Search and Git, with Profiles first of Appearance", () =>
            SettingsPage!.SectionsShown.ToList() is { Count: > 0 } shown && shown[0] == EditorSettingsCatalog.TEXT
            && shown.IndexOf(EditorSettingsCatalog.CODE) == 1 && shown.IndexOf(EditorSettingsCatalog.FILES) == 2 && shown.IndexOf(EditorSettingsCatalog.SEARCH) == 3
            && shown.IndexOf(EditorSettingsCatalog.GIT) == 4
            && shown.IndexOf(EditorSettingsCatalog.PROFILES) > shown.IndexOf(EditorSettingsCatalog.DESIGNER)
            && shown.IndexOf(EditorSettingsCatalog.PROFILES) < shown.IndexOf(EditorSettingsCatalog.THEME));
        test.Check("settings looks: the git rows are under Editor · Git", () =>
            Group("git_blame") == EditorSettingsCatalog.GIT && Group("git_autofetch") == EditorSettingsCatalog.GIT && EditorSettingsCatalog.GIT == "Editor · Git");
        test.Check("settings looks: running and debugging are under Editor · Run and debug, the code's zoom a slider of times", () =>
            Group("debugger_path") == EditorSettingsCatalog.RUN && Group("run_to_terminal") == EditorSettingsCatalog.RUN
            && SettingsPage!.Settings.First(setting => setting.Key == "code_zoom") is { Slider: true, Unit: "×", Group: EditorSettingsCatalog.CODE });

        // The canvas under the window is a blur of colour through its glass, a panel of the layout came through as a box
        test.Check("settings looks: the window's glass is blurred and frosted enough that the canvas under it isn't a box behind the rows", () =>
            !Glassy || SettingsWindow?.Glass is { Blur: >= 48.0f, Clarity: <= 0.25f });
        test.Run(CloseSettings);
        test.Wait();

        /* Color Theme, from the File menu */

        string wasSkin = string.Empty, wasTheme = string.Empty, first = string.Empty;
        ListBox Chooser() => Inside<ListBox>(textDialog!.Body)!;
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            (wasSkin, wasTheme) = (settings.EditorSkin, settings.EditorTheme);
        });
        Pick("File", () => Said("settings_looks.color_theme"));
        test.Wait();
        test.Check("settings looks: Color Theme lists every theme, the one the editor's in first and chosen", () =>
            textDialog is { IsOpen: true } && Chooser() is { Index: 0 } list && list.Items.Length == EditorSkins().Count
            && (first = list.Items[0]) == EditorSkins().Find(choice => choice.Skin.Name == wasSkin && (choice.Theme ?? string.Empty) == wasTheme).Text);

        string shownTheme = string.Empty;
        test.Run(() =>
        {
            ListBox list = Chooser();
            int other = Array.FindIndex(list.Items, item => item != first && EditorSkins().Any(choice => choice.Text == item && choice.Skin.Name == TOOL_SKIN_NAME));
            list.Choose(other);
        });
        test.WaitFor(() => compositor.Skin is { } worn && worn.Theme != wasTheme, 2.0f);
        test.Run(() =>
        {
            shownTheme = settings.EditorTheme;
            textDialog!.Cancel();
        });
        test.WaitFor(() => compositor.Skin is { } worn && worn.Theme == wasTheme, 2.0f);
        test.Check("settings looks: Color Theme shows a theme as it's moved to and Escape puts the old one back", () =>
            shownTheme.Length > 0 && shownTheme != wasTheme && settings.EditorSkin == wasSkin && settings.EditorTheme == wasTheme
            && compositor.Skin?.Theme == wasTheme && textDialog is not { IsOpen: true });

        // Enter keeps the one it's on, and the list closed after it doesn't put the old one back
        string kept = string.Empty;
        test.Run(ChooseColorTheme);
        test.Wait();
        test.Run(() =>
        {
            ListBox list = Chooser();
            int other = Array.FindIndex(list.Items, item => item != first && EditorSkins().Any(choice => choice.Text == item && choice.Skin.Name == TOOL_SKIN_NAME));
            list.Choose(other);
            Inside<TextBox>(textDialog!.Body)!.OnSubmitted!(string.Empty);
            kept = settings.EditorTheme;
        });
        test.WaitFor(() => compositor.Skin is { } worn && worn.Theme == kept, 2.0f);
        test.Check("settings looks: and Enter keeps the one it's on", () =>
            kept.Length > 0 && kept != wasTheme && settings.EditorTheme == kept && compositor.Skin?.Theme == kept && textDialog is not { IsOpen: true });
        test.Run(() => UseEditorSkin(wasSkin, wasTheme, save: false));
        test.WaitFor(() => compositor.Skin is { } worn && worn.Theme == wasTheme, 2.0f);

        /* The settings file in a tab */

        string file = string.Empty;
        HexDocument? before = null;
        test.Run(() =>
        {
            before = document;
            file = Path.Combine(testFolder, "selftest_settings.hor");
            File.WriteAllText(file, "// The self test's settings file\nlet settings = {\n    editor: {\n        code_zoom: 1.25\n    }\n};\n");
            settingsFile = file;
        });
        Pick("File", () => Said("settings_looks.open_file"));
        test.Wait();
        test.Check("settings looks: Open Settings File opens the file in a tab", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file) && documents.Contains(document) && document.Editor.Text.Contains("code_zoom"));
        test.Run(() =>
        {
            if (document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file))
                Close();
            settingsFile = null;
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();

        TestTextSettings(test);
        TestSettingsNav(test);
    }
}
