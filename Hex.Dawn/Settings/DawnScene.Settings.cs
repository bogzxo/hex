using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// What the editor is set to (HexSettings), read as it starts and saved as it changes. The window it's set in is
// SettingsPage, its skin is in EditorSkin (and picked from a list in ThemePick), the edges of its panels in Sides. The
// file itself opens in a tab too and is read again as it's saved, see SettingsFile. The folder that's open can have
// settings of its own over the user's, read again here whenever it's another folder, see SettingsScope.
internal sealed partial class DawnScene
{
    private HexSettings settings = null!;
    /// <summary>
    /// Helper method to read the settings and put them in force, the theme of the editor, the sizes its panels were
    /// left at, and to give the panels the edges they are dragged by. Once, as the editor is made.
    /// </summary>
    private void LoadSettings()
    {
        // The editor testing itself, or only checking layouts, comes up the same every time and keeps nothing
        bool kept = !options.SelfTest && options.Check.Length == 0;

        // A file that declares no settings says the skeleton it needs, the way Hex writes it, and what's wrong with
        // the files by hand is underlined in them and listed in Problems (SettingsDiagnostics)
        SettingsSections.Missing = Said("settings_problems.no_settings", ("skeleton", Said("settings_problems.skeleton")));
        diagnosticSources.Add(SettingsDiagnosticsOf);
        settings = new HexSettings(kept ? Horizon.UI.Components.TerminalFiles.Settings() : null, Path.Combine(AppContext.BaseDirectory, HexSettings.FILE));

        layersScroll = layout.Get<ScrollPanel>("layers_scroll");
        usualLeft = treeScroll.Size.X;
        usualRight = inspectorScroll.Size.X;
        usualLayers = layersScroll.Size.Y;

        ApplyPanelSizes(instantly: true);
        UseEditorSkin(settings.EditorSkin, settings.EditorTheme, save: false);

        // The edges between the parts of the editor, a tab on each to be found by. The hold is nearly all on the
        // outside, in the gap to the next panel, the inside of these edges is where their scroll bars are
        SideGrip(treeScroll, UIResize.Right, left: true);
        SideGrip(inspectorScroll, UIResize.Left, left: false);
        Grip(layersScroll, UIResize.Top, new Vector2(0.0f, MIN_LAYERS), new Vector2(0.0f, MAX_LAYERS), size => settings.LayersHeight = size.Y);

        // The file read again as it's saved here or changed anywhere else. The self test watches a file of its own
        documentSaved += SettingsFileSaved;
        if (kept)
            WatchSettingsFile();
    }

    private void SaveSettings()
    {
        if (settings.Save() is { } problem)
            Say(problem, error: true);
        else
            KnowSettingsFile();
    }

    private void AddSettingsCommands()
    {
        // From the code too, Ctrl+, types nothing, and with the editor having the keyboard it did nothing at all
        commands.Add("File/settings/prefs:main", Said("settings_window.open"), OpenSettings, "Ctrl+,", "settings-gear", UIKeyUse.BoundWhileTyping);
        commands.Add("File/settings/prefs:main", Said("settings_looks.color_theme"), ChooseColorTheme, icon: "symbol-color");
        AddLooks(commands.AddMenu("File/settings/prefs:main", Said("looks.menu"), "color-mode"));
        commands.Add("File/settings/prefs:files", Said("settings_looks.open_file"), OpenSettingsFile, icon: "file-code");
        commands.Add("File/settings/prefs:files", Said("settings_scope.open_workspace_file"), OpenWorkspaceSettingsFile, icon: "file-code");
    }

    /* The folder's own settings */

    // The settings and the folder the folder's settings were last read for, read again when either is another
    private HexSettings? workspaceSettingsOf;
    private string? workspaceSettingsFolder;

    /// <summary>
    /// The folder's settings read for the folder that's open now, the last one's let go of, and everything they change
    /// put in force as the settings file being saved does, the open files indented again and the window's rows showing
    /// it. A folder's file that doesn't read says so on a card the way the user's does, and is false.
    /// </summary>
    private bool ReloadWorkspaceSettings()
    {
        string? folder = WorkspaceSettingsFolder();
        workspaceSettingsOf = settings;
        workspaceSettingsFolder = folder;

        HexSettings was = settings.Snapshot();
        string? error = settings.LoadWorkspace(folder);
        WatchWorkspaceSettingsFile(settings.WorkspacePath);
        if (error is not null && folder is not null)
            Notify(Said("settings_window.broken", ("name", WorkspaceFileName(folder)), ("error", error)), error: true);

        ApplyReadSettings(was);
        CheckSettingsFiles();
        RefreshScopeBar();
        settingsView?.Refresh();
        return error is null;
    }

    /// <summary>
    /// The folder's settings read again when the folder that's open is another, or the settings are (the file read
    /// again, the self test's own). Every tick, nothing to do nearly all of them.
    /// </summary>
    private void UpdateWorkspaceSettings()
    {
        string? folder = WorkspaceSettingsFolder();
        if (ReferenceEquals(workspaceSettingsOf, settings) && string.Equals(folder, workspaceSettingsFolder, StringComparison.Ordinal))
            return;

        // No folder before or now, nothing to read or let go of
        if (folder is null && settings.WorkspacePath is null)
        {
            workspaceSettingsOf = settings;
            workspaceSettingsFolder = null;
            WatchWorkspaceSettingsFile(null);
            return;
        }

        ReloadWorkspaceSettings();
    }

    // What the folder's file is called where it's said, the folder with it
    private static string WorkspaceFileName(string folder) => Path.Combine(FolderName(folder), HexSettings.WORKSPACE_FOLDER, HexSettings.WORKSPACE_FILE);
}
