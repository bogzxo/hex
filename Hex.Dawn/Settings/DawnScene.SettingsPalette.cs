using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// The settings in the command palette the way VS Code has them, Preferences and what it does. The window, the user's
// file and the folder's, the settings somebody changed, VS Code's settings brought over, every section of the window by
// its name, and every switch of the editor's flipped without opening anything. Only the palette has them, the menus have the few that matter.
internal sealed partial class DawnScene
{
    /// <summary>Every Preferences command, for the palette.</summary>
    private List<(string Text, Action Picked)> SettingsPaletteCommands()
    {
        var commands = new List<(string Text, Action Picked)>
        {
            (Said("settings_palette.open_ui"), () => OpenSettings(null)),
            (Said("settings_palette.open_user_file"), OpenSettingsFile),
            (Said("settings_palette.open_workspace_file"), OpenWorkspaceSettingsFile),
            (Said("settings_palette.show_modified"), ShowModifiedSettings),
            (Said("settings_palette.arrange_backdrop"), ArrangeBackdrop),
            (Said("settings_import.palette_user"), ImportVsCodeSettings),
            (Said("settings_import.palette_workspace"), ImportVsCodeWorkspaceSettings)
        };

        foreach (string section in SettingsPaletteSections())
            commands.Add((Said("settings_palette.open_section", ("section", section)), () => OpenSettings(section)));

        foreach (UISetting row in EditorSettingsCatalog.Settings)
        {
            if (row.Kind == UIFormKind.Flag)
                commands.Add((Said("settings_palette.toggle", ("title", row.Title)), () => ToggleSetting(new EditorSource(this), row)));
        }

        foreach (UISetting row in EditorSettingsCatalog.Text)
        {
            if (row.Kind == UIFormKind.Flag)
                commands.Add((Said("settings_palette.toggle", ("title", row.Title)), () => ToggleSetting(new TextSettingsSource(this), row)));
        }

        return commands;
    }

    // The sections of the window in the order it has them, the editor's, the terminal's, how Hex looks and the extensions'
    private static IEnumerable<string> SettingsPaletteSections()
    {
        var sections = new List<string>(EditorSettingsCatalog.Sections) { PANELS, KEYS_SECTION };
        foreach (TerminalSettingInfo setting in TerminalSettings.Described)
        {
            if (setting.Key != "theme")
                sections.Add("Terminal · " + TerminalSettingsForm.Group(setting.Key));
        }

        sections.Add(EXTENSIONS);

        // By their part first, the way the window goes down, then as they came
        int PartOf(string section) => Array.FindIndex(EditorSettingsCatalog.Parts, part => section.StartsWith(part, StringComparison.Ordinal)) is var at and >= 0 ? at : EditorSettingsCatalog.Parts.Length;
        return sections.Distinct(StringComparer.Ordinal).Select((section, at) => (section, at)).OrderBy(pair => PartOf(pair.section)).ThenBy(pair => pair.at).Select(pair => pair.section);
    }

    /// <summary>A switch of the editor's flipped as its row would, the row showing it when the window's up and the status bar saying which way.</summary>
    private void ToggleSetting(ISettingsSource source, UISetting row)
    {
        bool on = source.Get(row.Key) is not BooleanValue { Value: true };
        source.Set(row.Key, Values.Bool(on));
        SettingsPage?.Refresh(row.Key);
        Say(Said(on ? "settings_palette.now_on" : "settings_palette.now_off", ("title", row.Title)));
    }
}
