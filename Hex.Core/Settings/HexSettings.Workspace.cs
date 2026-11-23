using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Core;

// Workspace settings, as in VS Code. A .hex\settings.hor in the first folder of the explorer, the same settings object as
// the user settings with only an editor section, holding only the keys somebody set for that folder. They override the
// user settings while the folder is open and are never written into them, an overridden key keeps its user value there.
// How code is typed and drawn, git and running have workspace scope. How Hex looks, its extensions, its language
// servers and its panels are user settings only.
internal sealed partial class HexSettings
{
    /// <summary>The folder a project's workspace settings are kept in, and the file in it.</summary>
    public const string WORKSPACE_FOLDER = ".hex", WORKSPACE_FILE = "settings.hor";

    // The keys with workspace scope
    private static readonly HashSet<string> WorkspaceKeys = new(StringComparer.Ordinal)
    {
        "tab_size", "insert_spaces", "detect_indentation", "line_numbers", "indent_guides", "editorconfig",
        "rulers", "render_whitespace", "cursor_style", "sticky_scroll", "format_on_save", "format_on_type",
        "wrap_code", "minimap", "inlay_hints", "code_lens_references", "semantic_highlighting", "code_font", "code_zoom",
        "trim_whitespace", "final_newline", "auto_save", "auto_save_delay",
        "files_exclude", "search_exclude", "search_use_ignore_files", "explorer_auto_reveal",
        "git_blame", "git_autofetch", "git_autofetch_interval", "git_pull_mode", "git_account", "git_confirm_sync", "git_smart_commit",
        "git_subject_length", "git_line_length",
        "edit_predictions_workspace", EDIT_PREDICTIONS_EXCLUDE,
        "run_to_terminal", "debugger_path", "snap", "canvas_handles", LANGUAGE_OVERRIDES
    };

    /// <summary>The key of the agent data folder, which no folder's settings and no agent (hex_settings_set) can change.</summary>
    public const string AGENT_DATA_FOLDER = "agent_data_folder";

    /// <summary>Whether a setting is only the person's to change by hand, never through an agent's hex_settings_set.</summary>
    public static bool AgentsCannotSet(string key) => key == AGENT_DATA_FOLDER || key == DISCORD_PRESENCE;

    /// <summary>
    /// The key the languages files of the folder were set to by hand are kept under (Change Language Mode), only ever in
    /// the workspace settings, by their path in the folder.
    /// </summary>
    public const string LANGUAGE_OVERRIDES = "language_overrides";

    /// <summary>The language each file was set to by hand, by its full path, the folder's.</summary>
    public readonly Dictionary<string, string> LanguageOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times <see cref="LanguageOverrides"/> changed, for whoever applies them to know to look again.</summary>
    public int LanguageOverridesChanged { get; private set; }

    // The folder a workspace file is for, the one over its .hex
    private string? WorkspaceFolder => WorkspacePath is { } at ? Path.GetDirectoryName(Path.GetDirectoryName(at)) : null;

    /// <summary>
    /// A file's language set by hand, null to let it be worked out again, and the workspace settings written with it when a
    /// folder is open. Says what went wrong, null when fine.
    /// </summary>
    public string? SetLanguageOverride(string fullPath, string? languageId)
    {
        string full = Path.GetFullPath(fullPath);
        if (languageId is null)
            LanguageOverrides.Remove(full);
        else
            LanguageOverrides[full] = languageId;
        LanguageOverridesChanged++;

        if (WorkspacePath is null)
            return null;
        return LanguageOverrides.Count == 0 ? RemoveWorkspace(LANGUAGE_OVERRIDES) : SetWorkspace(LANGUAGE_OVERRIDES, LanguageOverridesObject().ToValue());
    }

    // Helper method to read the languages files were set to, the folder's paths made whole again
    private void ReadLanguageOverrides(Dictionary<string, IRuntimeValue> values)
    {
        if (!values.TryGetValue(LANGUAGE_OVERRIDES, out var value) || value is not ObjectValue byPath)
            return;

        LanguageOverrides.Clear();
        string? folder = WorkspaceFolder;
        foreach (var (path, said) in byPath.Properties)
        {
            if (said is not StringValue { Value.Length: > 0 } id)
                continue;

            try
            {
                string full = Path.IsPathRooted(path) || folder is null ? path : Path.Combine(folder, path);
                LanguageOverrides[Path.GetFullPath(full)] = id.Value;
            }
            catch (Exception)
            {
                // A path that makes no sense is no file's
            }
        }

        LanguageOverridesChanged++;
    }

    // Helper method to write the languages files were set to, by their path in the folder when they're in it
    private HIDLObject LanguageOverridesObject()
    {
        string? folder = WorkspaceFolder;
        var byPath = new HIDLObject();
        foreach (var (full, id) in LanguageOverrides.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string written = folder is not null && PlatformPaths.IsUnder(full, folder) ? Path.GetRelativePath(folder, full) : full;
            byPath.Add(written.Replace('\\', '/'), id);
        }

        return byPath;
    }

    // The editor section of the user's file as it was read, and what the user had for every setting the folder covers.
    // A setting the folder sets is written to the user's file from here, and goes back to this when the folder lets go
    private Dictionary<string, IRuntimeValue> userValues = new(StringComparer.Ordinal);

    // Every default value, made the first time it's asked for
    private static Dictionary<string, IRuntimeValue>? usual;

    private readonly Dictionary<string, IRuntimeValue> workspaceValues = new(StringComparer.Ordinal);

    /// <summary>The workspace settings file, null while no folder is open. It needn't exist.</summary>
    public string? WorkspacePath { get; private set; }

    /// <summary>The workspace values by key, only the keys somebody set there.</summary>
    public IReadOnlyDictionary<string, IRuntimeValue> WorkspaceValues => workspaceValues;

    /// <summary>Whether a key has workspace scope.</summary>
    public static bool IsWorkspaceKey(string key) => WorkspaceKeys.Contains(key);

    /// <summary>Where a folder keeps its workspace settings.</summary>
    public static string WorkspacePathOf(string folder) => Path.Combine(folder, WORKSPACE_FOLDER, WORKSPACE_FILE);

    /// <summary>Whether the workspace settings override a key.</summary>
    public bool IsOverridden(string key) => workspaceValues.ContainsKey(key);

    /// <summary>Every setting's default value, by key.</summary>
    public static IReadOnlyDictionary<string, IRuntimeValue> Usual => usual ??= new HexSettings(null).EditorValues();

    // Whether a value is a setting's default value, by how it's written
    private static bool IsUsual(string key, IRuntimeValue value)
    {
        if (!Usual.TryGetValue(key, out IRuntimeValue? comes))
            return false;
        try
        {
            return HIDLWriter.Write(value) == HIDLWriter.Write(comes);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A setting's user value, whatever the workspace says, the default value when the user never changed it.</summary>
    public IRuntimeValue? UserValue(string key) =>
        IsOverridden(key) ? userValues.GetValueOrDefault(key) ?? Usual.GetValueOrDefault(key) : EditorValues().GetValueOrDefault(key);

    /// <summary>
    /// The user value of a setting the workspace overrides, kept for the user settings while the workspace value stays in
    /// effect. Null resets the user value to its default. Saving is the caller's.
    /// </summary>
    public void SetUser(string key, IRuntimeValue? value)
    {
        if (value is null)
            userValues.Remove(key);
        else
            userValues[key] = value;
    }

    /// <summary>
    /// Loads a folder's workspace settings, null for none, dropping the last folder's first. Says what went wrong when
    /// the file is invalid, null when it's valid or missing. An invalid file changes nothing when it's the one already
    /// loaded, and a new folder's starts with no overrides.
    /// </summary>
    public string? LoadWorkspace(string? folder)
    {
        string? at = folder is null ? null : WorkspacePathOf(folder);
        Dictionary<string, IRuntimeValue>? read = null;
        string? error = null;
        if (at is not null && SettingsSections.TryRead(at, out error) is { } all)
            read = SettingsSections.Section(all, SettingsSections.EDITOR);

        bool same = at is not null && WorkspacePath is not null && PlatformPaths.Same(at, WorkspacePath);
        if (error is not null && same)
            return error;

        // The user's own back for everything the last folder had
        var own = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (string key in workspaceValues.Keys)
        {
            if ((userValues.GetValueOrDefault(key) ?? Usual.GetValueOrDefault(key)) is { } value)
                own[key] = value;
        }

        Apply(own);
        workspaceValues.Clear();
        WorkspacePath = at;
        if (read is null)
            return error;

        // What the user has for each of them kept before the folder's go over it
        Dictionary<string, IRuntimeValue> now = EditorValues();
        foreach (var (key, value) in read)
        {
            // A folder can share less on Discord, never more
            if (!IsWorkspaceKey(key) && !DiscordFolderMaySet(key, value))
                continue;

            if (now.TryGetValue(key, out IRuntimeValue? users))
                userValues[key] = users;
            workspaceValues[key] = value;
        }

        Apply(workspaceValues);
        return null;
    }

    /// <summary>
    /// A setting set in the workspace settings and saved, the user value kept. The caller applies the setting itself,
    /// the way a row would, so whatever else it changes changes too. Says what went wrong, null when fine.
    /// </summary>
    public string? SetWorkspace(string key, IRuntimeValue value)
    {
        if (!IsWorkspaceKey(key) || WorkspacePath is null)
            return null;

        if (!IsOverridden(key) && EditorValues().TryGetValue(key, out IRuntimeValue? users))
            userValues[key] = users;
        workspaceValues[key] = value;
        return SaveWorkspace();
    }

    /// <summary>
    /// Removes a workspace override so the user value applies again. The caller applies the user value (UserValue, asked
    /// before this). Says what went wrong, null when fine.
    /// </summary>
    public string? RemoveWorkspace(string key) => workspaceValues.Remove(key) ? SaveWorkspace() : null;

    /// <summary>
    /// Writes the workspace settings file with its overrides and nothing else, creating its folder when it doesn't exist. Says
    /// what went wrong, null when fine.
    /// </summary>
    public string? SaveWorkspace()
    {
        if (WorkspacePath is not { } at)
            return null;

        var editor = new HIDLObject();
        foreach (var (key, value) in workspaceValues)
            editor.Add(key, value);
        if (editor.Count == 0)
            editor.Note("tab_size: 2, workspace editor settings that override the user settings");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(at)!);
            DocumentFile.Write(at, SettingsSections.Write(at, (SettingsSections.EDITOR, editor)), new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception e)
        {
            return $"couldn't keep the folder's settings: {e.Message}";
        }
    }

    /// <summary>A copy of the settings as they are now, to tell what changed once a folder's are read.</summary>
    public HexSettings Snapshot() => (HexSettings)MemberwiseClone();
}
