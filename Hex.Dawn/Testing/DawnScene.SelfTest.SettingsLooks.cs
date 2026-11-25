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
        test.Check("settings looks: the editor comes first, then the terminal, how Hex looks, AI, Link and the extensions", () =>
            SettingsPage!.Settings.Select(setting => setting.Group.Split(" · ")[0]).Distinct().SequenceEqual(["Editor", "Terminal", "Appearance", "Performance", "AI", "Link", "Extensions"])
            && SettingsPage.Settings.Where(setting => setting.Group == EditorSettingsCatalog.THEME).Select(setting => setting.Key).SequenceEqual(["editor_skin", "theme"]));
        test.Check("settings looks: the sections go Editor · Text first, then Caret, Code, Save and format, Files and Git, with Looks first of Appearance", () =>
            SettingsPage!.SectionsShown.ToList() is { Count: > 0 } shown && shown[0] == EditorSettingsCatalog.TEXT
            && shown.IndexOf(EditorSettingsCatalog.CARET) == 1 && shown.IndexOf(EditorSettingsCatalog.CODE) == 2 && shown.IndexOf(EditorSettingsCatalog.SAVE) == 3 && shown.IndexOf(EditorSettingsCatalog.FILES) == 4
            && shown.IndexOf(EditorSettingsCatalog.GIT) == 5
            && shown.IndexOf(EditorSettingsCatalog.LOOKS) > shown.IndexOf(EditorSettingsCatalog.DESIGNER)
            && shown.IndexOf(EditorSettingsCatalog.LOOKS) < shown.IndexOf(EditorSettingsCatalog.THEME));
        test.Check("settings looks: the git rows are under Editor · Git", () =>
            Group("git_blame") == EditorSettingsCatalog.GIT && Group("git_autofetch") == EditorSettingsCatalog.GIT && EditorSettingsCatalog.GIT == "Editor · Git");
        test.Check("settings looks: running and debugging are under Editor · Run and debug, the code's zoom a slider of times", () =>
            Group("debugger_path") == EditorSettingsCatalog.RUN && Group("run_to_terminal") == EditorSettingsCatalog.RUN
            && SettingsPage!.Settings.First(setting => setting.Key == "code_zoom") is { Slider: true, Unit: "×", Group: EditorSettingsCatalog.TEXT });
        // Agents, prediction and Link each have a home of their own, and a row that moved is still found by the section it was in
        test.Check("settings looks: agents, Local AI and Link have their own sections, the system files row is Link · Sync", () =>
            Group("mcp_server") == EditorSettingsCatalog.AGENTS && Group("no_ai") == EditorSettingsCatalog.AGENTS && Group(EditorSettingsCatalog.LOCAL_AI_WINDOW) == EditorSettingsCatalog.AI
            && !SettingsPage!.Settings.Any(setting => EditorSettingsCatalog.IsLocalAiWindows(setting.Key))
            && SettingsPage.Settings.Where(setting => setting.Group == EditorSettingsCatalog.AI).Select(setting => setting.Key).SequenceEqual([EditorSettingsCatalog.LOCAL_AI_WINDOW])
            && Group("link") == EditorSettingsCatalog.LINK && Group("link_system_file_folders") == EditorSettingsCatalog.LINK_SYNC
            && Group("interface_size") == EditorSettingsCatalog.INTERFACE && Group("caret_glide") == EditorSettingsCatalog.CARET && Group("visual_studio_keys") == EditorSettingsCatalog.KEYS
            && Group("link_system_file_folders") is { } syncGroup && SettingsPage!.SectionsShown.Contains(syncGroup));
        test.Run(() => SettingsPage!.Find("run and debug"));
        test.Settle(0.3f);
        test.Check("settings looks: the old section words still find the rows that moved out of them", () =>
            SettingsPage!.IsRowShown("link") && SettingsPage.IsRowShown("mcp_server") && SettingsPage.IsRowShown("debugger_path"));
        test.Run(() => SettingsPage!.Find("motion"));
        test.Settle(0.3f);
        test.Check("settings looks: motion finds the caret's glide and the scroll speed", () =>
            SettingsPage!.IsRowShown("caret_glide") && SettingsPage.IsRowShown("scroll_speed"));
        // Local AI's settings are the Local AI window's; a search for one of them finds the row that opens it
        test.Run(() => SettingsPage!.Find("temperature"));
        test.Settle(0.3f);
        test.Check("settings looks: temperature finds the one row that opens the Local AI window", () =>
            HexAi.Off || SettingsPage!.IsRowShown(EditorSettingsCatalog.LOCAL_AI_WINDOW) && SettingsPage.RowsShown == 1);
        test.Run(() => SettingsPage!.Find("llama"));
        test.Settle(0.3f);
        test.Check("settings looks: llama finds the row that opens the Local AI window", () =>
            HexAi.Off || SettingsPage!.IsRowShown(EditorSettingsCatalog.LOCAL_AI_WINDOW));
        // Only the window leaves them out: MCP's hex_settings_get still reads them, by key and by find, and hex_settings_set still knows them
        string McpSaid(string name, System.Text.Json.Nodes.JsonObject arguments) =>
            (name == "get" ? SettingsGet(arguments) : SettingsSet(arguments)).OfType<McpToolResult>().FirstOrDefault()?.AllText() ?? string.Empty;
        test.Check("settings looks: hex_settings_get still finds local_ai_predict_model and llamacpp_port, which the window doesn't show", () =>
            !SettingsPage!.Settings.Any(setting => setting.Key is "local_ai_predict_model" or "llamacpp_port")
            && McpSaid("get", new() { ["key"] = "local_ai_predict_model" }).StartsWith("local_ai_predict_model = ", StringComparison.Ordinal)
            && McpSaid("get", new() { ["key"] = "llamacpp_port" }).StartsWith("llamacpp_port = ", StringComparison.Ordinal)
            && McpSaid("get", new() { ["find"] = "edit_predictions_temperature" }).Contains("edit_predictions_temperature = ", StringComparison.Ordinal)
            && McpSaid("get", new() { ["find"] = "ai_edit" }).Contains("ai_edit_max_tokens = ", StringComparison.Ordinal)
            && !McpSaid("get", new() { ["key"] = EditorSettingsCatalog.LOCAL_AI_WINDOW }).Contains(" = ", StringComparison.Ordinal));
        // hex_settings_set still knows them: a value of the wrong kind is refused as that kind, not as a setting there's none of
        test.Check("settings looks: hex_settings_set still knows llamacpp_port", () =>
            McpSaid("set", new() { ["key"] = "llamacpp_port", ["value"] = "not a number" }) is var said && said.Contains("a number", StringComparison.Ordinal) && !said.Contains("There's no setting", StringComparison.Ordinal));
        test.Run(() => SettingsPage!.Find(string.Empty));

        // Liquid glass: what's under the window comes through with clear low-frost refraction,
        // and slightly less clear than a popup's so a page of sentences on it still reads
        test.Check("settings looks: the window's glass lets what's under it through, clear and a little less clear than a popup's", () =>
            !Glassy || SettingsWindow?.Glass is { Blur: >= 0.0f } glass && glass.Clarity < settings.GlassClarity && glass.Clarity > 0.25f);
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
