using System.Diagnostics;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The settings file and the settings window as one thing, VS Code's Open Settings (JSON) and back. The window's { }
// opens the file at the line of the row the keyboard was on, Ctrl+, in the file opens the window at the setting the
// caret is on. The file is read again the moment it's saved in Hex, or changed by anything else (Dusk, another editor),
// and put in force without a restart. A file that doesn't read says why and on which line, and the settings stay as
// they were, it never drops them back to what they come as.
internal sealed partial class DawnScene
{
    // How long the file is left alone after a change before it's read, an editor saving writes it two or three times
    private const double SETTINGS_SETTLE = 0.2;

    // The file the settings are kept in, worked out the first time it's asked for. The self test gives it one of its own,
    // the editor testing itself keeps nothing and doesn't open anybody's real one
    private string? settingsFile;

    private string SettingsFilePath => settingsFile ??= TerminalFiles.Settings();

    // The file as it was last read or written by Hex itself, a change that leaves it the same is nothing to take in
    private string? settingsKnown;

    // Whether the { } said yet that Ctrl+, goes back, once a run is enough
    private bool settingsBackSaid;

    // What watches the file for a change from outside, the file it watches, and when it last heard of one
    private FileSystemWatcher? settingsWatcher;
    private string? settingsWatched;
    private long settingsChangedAt;
    private int settingsChanged;

    /// <summary>
    /// The settings file in a tab like any other, VS Code's Open User Settings (JSON), for whoever would rather type than
    /// click. Written out first if there's nothing there yet, the editor's settings as they are now.
    /// </summary>
    private void OpenSettingsFile() => OpenSettingsFileAt(null);

    /// <summary>
    /// The settings file with the caret on the line of a setting, the one of its key inside its section (the editor's
    /// unless said), the section's own line when the file hasn't got the key, the top when it hasn't got the section.
    /// </summary>
    private void OpenSettingsFileAt(string? key, string section = SettingsSections.EDITOR)
    {
        string path = SettingsFilePath;
        if (!File.Exists(path))
            SaveSettings();

        if (!File.Exists(path))
        {
            Say(Said("settings_looks.no_file", ("path", path)), error: true);
            return;
        }

        // The text open in a tab when it is, its lines might not be the disk's yet
        string full = Path.GetFullPath(path);
        HexDocument? open = documents.Find(other => other.Path is { } at && PlatformPaths.Same(at, full));
        string text;
        try
        {
            text = open?.Editor.Text ?? File.ReadAllText(path);
        }
        catch (Exception)
        {
            text = string.Empty;
        }

        var (line, column) = key is null ? (0, 0) : LineOfSetting(text, section, key);
        OpenAt(path, line, column);
    }

    /// <summary>
    /// Where a setting is written in the text of a settings file, its line and the column its key starts at (from 0), by
    /// its section and its key. The section's own line when the key isn't in it, the top when there's no such section.
    /// An old file of the terminal's keys at the top is searched all through.
    /// </summary>
    internal static (int Line, int Column) LineOfSetting(string text, string section, string key)
    {
        string[] lines = text.Split('\n');
        int start = -1, depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (start < 0)
            {
                if (KeyOf(line) == section && line.Contains('{'))
                {
                    start = i;
                    depth = Braces(line);
                }

                continue;
            }

            // Only the keys right inside the section, a key of the same name in an object inside it isn't the setting
            if (depth == 1 && KeyOf(line) == key)
                return (i, line.Length - line.TrimStart().Length);

            depth += Braces(line);
            if (depth <= 0)
                break;
        }

        if (start >= 0)
            return (start, lines[start].Length - lines[start].TrimStart().Length);

        // No section, an old file or one written by hand, the first line that sets the key anywhere
        for (int i = 0; i < lines.Length; i++)
        {
            if (KeyOf(lines[i]) == key)
                return (i, lines[i].Length - lines[i].TrimStart().Length);
        }

        return (0, 0);
    }

    /// <summary>The key a line of an object sets, the name in front of its colon, null for a line that sets none.</summary>
    internal static string? KeyOf(string line)
    {
        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        int end = 0;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] == '_'))
            end++;

        if (end == 0 || char.IsDigit(trimmed[0]))
            return null;

        ReadOnlySpan<char> rest = trimmed[end..].TrimStart();
        return rest.Length > 0 && rest[0] == ':' ? trimmed[..end].ToString() : null;
    }

    // How many more braces a line opens than it closes, the ones in its strings and after its // left out
    private static int Braces(string line)
    {
        int count = 0;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    quoted = false;
                continue;
            }

            if (c == '"')
                quoted = true;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;
            else if (c == '{')
                count++;
            else if (c == '}')
                count--;
        }

        return count;
    }

    /// <summary>
    /// The window's { }, the file at the row the keyboard was on last, or the first setting of the section that's shown
    /// when it was on none. The window goes, the file is what's being looked at now, as VS Code's settings tab turns into
    /// the file.
    /// </summary>
    private void OpenSettingsFileFromWindow()
    {
        if (settingsView is not { } view)
        {
            OpenSettingsFile();
            return;
        }

        string? key = view.RowKey;
        if (key is null && view.Page.CurrentGroup is { } group)
            key = view.Page.Settings.FirstOrDefault(setting => setting.Group == group && !setting.ButtonsOnly)?.Key;

        string section = key is null ? SettingsSections.EDITOR : view.SectionOf(key) ?? SettingsSections.EDITOR;
        CloseSettings();
        OpenSettingsFileAt(key, section);

        // Once a run, the way back is said, the file has no button for it
        if (!settingsBackSaid)
        {
            settingsBackSaid = true;
            Say(Said("settings_palette.back_to_window"));
        }
    }

    // The row of a key the file has, the theme's second half is the theme row and the panels' sizes the panels'
    private static string RowKeyOf(string fileKey) => fileKey switch
    {
        "editor_theme" => "editor_skin",
        "left_width" or "right_width" or "layers_height" or "panel_height" => "panels",
        _ => fileKey
    };

    /// <summary>The setting on the caret's line when the file on screen is the settings file, null otherwise.</summary>
    private string? SettingAtCaret()
    {
        if (document is not { IsText: true, Path: { } path } || !PlatformPaths.Same(Path.GetFullPath(path), Path.GetFullPath(SettingsFilePath)))
            return null;

        int line = document.Editor.Document.Caret.Line;
        return KeyOf(document.Editor.Document.LineAt(line)) is { } key ? RowKeyOf(key) : null;
    }

    /* Reading it again */

    /// <summary>
    /// Starts reading the file again as it changes, Hex's own saves of it and anybody else's. Once for a file, a second
    /// call for the same one does nothing, one for another file watches that instead.
    /// </summary>
    private void WatchSettingsFile()
    {
        string path = Path.GetFullPath(SettingsFilePath);
        settingsKnown = ReadSettingsText(path);
        if (settingsWatched is not null && PlatformPaths.Same(settingsWatched, path))
        {
            CheckSettingsFiles();
            return;
        }

        StopWatchingSettings();
        settingsWatched = path;
        CheckSettingsFiles();
        if (Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder))
            return;

        try
        {
            var watcher = new FileSystemWatcher(folder, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            watcher.Changed += SettingsFileTouched;
            watcher.Created += SettingsFileTouched;
            watcher.Renamed += SettingsFileTouched;
            watcher.EnableRaisingEvents = true;
            settingsWatcher = watcher;
        }
        catch (Exception e)
        {
            Say(Said("settings_window.no_watch", ("name", Path.GetFileName(path)), ("reason", e.Message)), error: true);
        }
    }

    private void StopWatchingSettings()
    {
        settingsWatcher?.Dispose();
        settingsWatcher = null;
        Interlocked.Exchange(ref settingsChanged, 0);

        // What it had wrong goes with it, it isn't the settings file any more
        if (settingsWatched is { } was && settingsProblems.ContainsKey(was))
        {
            settingsWatched = null;
            CheckSettingsFiles();
        }

        settingsWatched = null;
    }

    // On a thread of the pool, noted for the editor's thread to take in once the burst settles
    private void SettingsFileTouched(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref settingsChangedAt, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref settingsChanged, 1);
        Horizon.Engine.GameEngine.Instance?.WindowManager.Wake();
    }

    /// <summary>A change to the file from outside taken in once it has settled. Every tick, nothing to do most of them.</summary>
    private void UpdateSettingsFile()
    {
        UpdateWorkspaceSettings();
        UpdateWorkspaceSettingsFile();
        if (Volatile.Read(ref settingsChanged) == 0)
            return;

        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref settingsChangedAt)).TotalSeconds < SETTINGS_SETTLE)
        {
            Horizon.Engine.GameEngine.Instance?.WindowManager.RequestFrames();
            return;
        }

        Interlocked.Exchange(ref settingsChanged, 0);
        TakeInSettingsFile();
    }

    /// <summary>A document saved, the settings read again when it's the settings file or the folder's.</summary>
    private void SettingsFileSaved(HexDocument saved)
    {
        if (saved.Path is not { } path)
            return;

        string full = Path.GetFullPath(path);
        if (settingsWatched is { } watched && PlatformPaths.Same(full, watched))
            TakeInSettingsFile();
        else if (settings.WorkspacePath is { } folders && PlatformPaths.Same(full, Path.GetFullPath(folders)))
            TakeInWorkspaceSettingsFile();
        else
            return;

        // Looked over again even when it's the same as Hex last read it, the underlines go back on after the save
        CheckSettingsFiles();
    }

    /// <summary>
    /// The file Hex just wrote, known so the watch doesn't read Hex's own save back in. What was wrong with it is looked
    /// over again when something was, a row changed may have put it right.
    /// </summary>
    private void KnowSettingsFile()
    {
        if (settingsWatched is not { } watched)
            return;

        settingsKnown = ReadSettingsText(watched);
        if (settingsProblems.ContainsKey(watched))
            CheckSettingsFiles();
    }

    private static string? ReadSettingsText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The file read again and put in force when it changed since Hex last saw it. One that doesn't read is said on a
    /// card, what HIDL said with its line, and nothing changes. One that does is the settings now, everything the window
    /// puts in force as a row changes put in force from it, and the window's rows showing it.
    /// </summary>
    private void TakeInSettingsFile()
    {
        if (settingsWatched is not { } path || ReadSettingsText(path) is not { } text || text == settingsKnown)
            return;

        settingsKnown = text;
        CheckSettingsFiles();
        string name = Path.GetFileName(path);
        if (SettingsSections.TryRead(path, out string? error) is null)
        {
            Notify(Said("settings_window.broken", ("name", name), ("error", error ?? string.Empty)), error: true);
            return;
        }

        // The folder's own go over the user's again, as they did over the ones before
        HexSettings was = settings;
        settings = new HexSettings(path);
        settings.LoadWorkspace(WorkspaceSettingsFolder());
        workspaceSettingsOf = settings;
        workspaceSettingsFolder = WorkspaceSettingsFolder();
        ApplyReadSettings(was);
        settingsView?.Refresh();
        Say(Said("settings_window.read_again", ("name", name)));
    }

    /// <summary>What changed between the settings before and the ones read now put in force, as their rows would.</summary>
    private void ApplyReadSettings(HexSettings was)
    {
        HexSettings now = settings;
        if (now.EditorSkin != was.EditorSkin || now.EditorTheme != was.EditorTheme)
            UseEditorSkin(now.EditorSkin, now.EditorTheme, save: false);

        foreach (HexDocument open in documents)
        {
            open.Editor.WordWrap = now.WrapCode;
            open.Editor.Minimap = now.Minimap;
            open.Editor.TextScale = CODE_TEXT * now.CodeZoom;
        }

        ApplyTextSettingsEverywhere();

        if (now.LeftWidth != was.LeftWidth || now.RightWidth != was.RightWidth || now.LayersHeight != was.LayersHeight || now.PanelHeight != was.PanelHeight)
            ApplyPanelSizes();
        if (now.CodeFont != was.CodeFont)
            codeEditing.ApplyCodeFont();
        if (now.InlayHints != was.InlayHints)
            ForgetInlays();
        if (now.SemanticHighlighting != was.SemanticHighlighting)
            ForgetSemantics();
        if (now.CSharpColors != was.CSharpColors)
            languages.ApplyCSharpColors();
        if (now.McpServer != was.McpServer)
            ApplyMcp();
        if (now.GitSmartCommit != was.GitSmartCommit)
            ((IPanels)this).RefreshSide(HexSide.SourceControl);

        // The picture read again only when which or whether changed, the rest of it is where and how much
        if (now.BackdropOn != was.BackdropOn || now.BackdropImage != was.BackdropImage || now.BackdropWrap != was.BackdropWrap)
            ApplyBackdrop();
        else
            ShowBackdropEverywhere();

        ApplyEffects();
    }

    /* The folder's own file */

    // What watches the folder's file, the folder it watches (its .hex, or the folder .hex would be made in while there's
    // none) and for what, the file as Hex last read or wrote it, and when a change was last heard of
    private FileSystemWatcher? workspaceWatcher;
    private string? workspaceWatched, workspaceWatchedFor, workspaceKnown;
    private long workspaceChangedAt;
    private int workspaceChanged;

    /// <summary>
    /// The folder's settings file in a tab, VS Code's Open Workspace Settings (JSON). Made first with an empty editor
    /// section when the folder hasn't got one. Says so when no folder is open to have settings of its own.
    /// </summary>
    private void OpenWorkspaceSettingsFile()
    {
        if (WorkspaceSettingsFolder() is not { } folder)
        {
            Say(Said("settings_scope.no_folder"), error: true);
            return;
        }

        if (settings.WorkspacePath is not { } path || !PlatformPaths.Same(path, HexSettings.WorkspacePathOf(folder)))
        {
            ReloadWorkspaceSettings();
            path = settings.WorkspacePath ?? HexSettings.WorkspacePathOf(folder);
        }

        if (!File.Exists(path))
        {
            if (settings.SaveWorkspace() is { } problem)
            {
                Say(problem, error: true);
                return;
            }

            KnowWorkspaceSettingsFile();
        }

        var (line, column) = LineOfSetting(ReadSettingsText(path) ?? string.Empty, SettingsSections.EDITOR, "tab_size");
        OpenAt(path, line, column);
    }

    /// <summary>
    /// Starts reading the folder's file again as it changes, null for no folder. Its .hex folder is watched when it's
    /// there, otherwise the folder it'd be made in, for .hex turning up. Watching the same again does nothing.
    /// </summary>
    private void WatchWorkspaceSettingsFile(string? path)
    {
        workspaceKnown = path is null ? null : ReadSettingsText(path);
        string? hex = path is null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
        bool made = hex is not null && Directory.Exists(hex);
        string? folder = made ? hex : hex is null ? null : Path.GetDirectoryName(hex);
        string? watchFor = made ? HexSettings.WORKSPACE_FILE : HexSettings.WORKSPACE_FOLDER;
        if (folder is not null && workspaceWatched is not null && PlatformPaths.Same(folder, workspaceWatched) && workspaceWatchedFor == watchFor)
            return;

        StopWatchingWorkspaceSettings();
        if (folder is null || !Directory.Exists(folder))
            return;

        workspaceWatched = folder;
        workspaceWatchedFor = watchFor;
        try
        {
            var watcher = new FileSystemWatcher(folder, watchFor) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size };
            watcher.Changed += WorkspaceSettingsTouched;
            watcher.Created += WorkspaceSettingsTouched;
            watcher.Deleted += WorkspaceSettingsTouched;
            watcher.Renamed += WorkspaceSettingsTouched;
            watcher.EnableRaisingEvents = true;
            workspaceWatcher = watcher;
        }
        catch (Exception e)
        {
            Say(Said("settings_window.no_watch", ("name", HexSettings.WORKSPACE_FILE), ("reason", e.Message)), error: true);
        }
    }

    private void StopWatchingWorkspaceSettings()
    {
        workspaceWatcher?.Dispose();
        workspaceWatcher = null;
        workspaceWatched = workspaceWatchedFor = null;
        Interlocked.Exchange(ref workspaceChanged, 0);
    }

    // On a thread of the pool, noted for the editor's thread to take in once the burst settles
    private void WorkspaceSettingsTouched(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref workspaceChangedAt, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref workspaceChanged, 1);
        Horizon.Engine.GameEngine.Instance?.WindowManager.Wake();
    }

    /// <summary>A change to the folder's file from outside taken in once it has settled. Every tick, nothing to do most of them.</summary>
    private void UpdateWorkspaceSettingsFile()
    {
        if (Volatile.Read(ref workspaceChanged) == 0)
            return;

        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref workspaceChangedAt)).TotalSeconds < SETTINGS_SETTLE)
        {
            Horizon.Engine.GameEngine.Instance?.WindowManager.RequestFrames();
            return;
        }

        Interlocked.Exchange(ref workspaceChanged, 0);
        TakeInWorkspaceSettingsFile();
    }

    /// <summary>The folder's file Hex just wrote, known so the watch doesn't read Hex's own save back in, and watched now it's there.</summary>
    private void KnowWorkspaceSettingsFile()
    {
        WatchWorkspaceSettingsFile(settings.WorkspacePath);
        if (settings.WorkspacePath is { } path && settingsProblems.ContainsKey(Path.GetFullPath(path)))
            CheckSettingsFiles();
    }

    /// <summary>
    /// The folder's file read again and put in force when it changed since Hex last saw it, or went. One that doesn't read
    /// is said on a card and nothing changes, the same as the user's.
    /// </summary>
    private void TakeInWorkspaceSettingsFile()
    {
        if (settings.WorkspacePath is not { } path)
            return;

        if (ReadSettingsText(path) == workspaceKnown)
        {
            // Nothing new in it, but a .hex that was just made is what's watched from now on
            KnowWorkspaceSettingsFile();
            return;
        }

        if (ReloadWorkspaceSettings())
            Say(Said("settings_window.read_again", ("name", HexSettings.WORKSPACE_FILE)));
    }
}
