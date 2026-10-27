using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Core;

// The folder that's open with settings of its own, VS Code's workspace settings. A .hex\settings.hor in the first folder
// of the explorer, the same settings object as the user's file with only an editor section, and only the keys somebody
// set for that folder in it. They go over the user's while the folder is open, and the user's file never gets them, a
// setting the folder covers is still written there as the user had it. How code is typed and drawn, git and running
// can be a folder's, how Hex looks, its extensions, its language servers and its panels stay the user's.
internal sealed partial class HexSettings
{
    /// <summary>The folder a project's own settings are kept in, and the file in it.</summary>
    public const string WORKSPACE_FOLDER = ".hex", WORKSPACE_FILE = "settings.hor";

    // The settings a folder can have its own way
    private static readonly HashSet<string> WorkspaceKeys = new(StringComparer.Ordinal)
    {
        "tab_size", "insert_spaces", "detect_indentation", "line_numbers", "indent_guides", "editorconfig",
        "rulers", "render_whitespace", "cursor_style", "sticky_scroll", "format_on_save",
        "wrap_code", "minimap", "inlay_hints", "code_lens_references", "semantic_highlighting", "code_font", "code_zoom",
        "trim_whitespace", "final_newline", "auto_save", "auto_save_delay",
        "files_exclude", "search_exclude", "search_use_ignore_files",
        "git_blame", "git_autofetch", "git_autofetch_interval", "git_pull_mode", "git_account", "git_confirm_sync", "git_smart_commit",
        "git_subject_length", "git_line_length",
        "run_to_terminal", "debugger_path", "snap", "canvas_handles", LANGUAGE_OVERRIDES
    };

    /// <summary>
    /// The key the languages files of the folder were set to by hand are kept under (Change Language Mode), only ever in
    /// the folder's own file, by their path in the folder.
    /// </summary>
    public const string LANGUAGE_OVERRIDES = "language_overrides";

    /// <summary>The language each file was set to by hand, by its full path, the folder's.</summary>
    public readonly Dictionary<string, string> LanguageOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times <see cref="LanguageOverrides"/> changed, for whoever puts them in force to know to look again.</summary>
    public int LanguageOverridesChanged { get; private set; }

    // The folder a workspace file is for, the one over its .hex
    private string? WorkspaceFolder => WorkspacePath is { } at ? Path.GetDirectoryName(Path.GetDirectoryName(at)) : null;

    /// <summary>
    /// A file's language set by hand, null to let it be worked out again, and the folder's file written with it when a
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

    // Everything at what it comes as, made the first time it's asked for
    private static Dictionary<string, IRuntimeValue>? usual;

    private readonly Dictionary<string, IRuntimeValue> workspaceValues = new(StringComparer.Ordinal);

    /// <summary>The folder's settings file, null while no folder is open. It needn't be there.</summary>
    public string? WorkspacePath { get; private set; }

    /// <summary>What the folder's file sets, by key, only the keys somebody set there.</summary>
    public IReadOnlyDictionary<string, IRuntimeValue> WorkspaceValues => workspaceValues;

    /// <summary>Whether a folder can have a setting its own way.</summary>
    public static bool IsWorkspaceKey(string key) => WorkspaceKeys.Contains(key);

    /// <summary>Where a folder keeps its own settings.</summary>
    public static string WorkspacePathOf(string folder) => Path.Combine(folder, WORKSPACE_FOLDER, WORKSPACE_FILE);

    /// <summary>Whether the folder's file sets a setting, which is then the folder's and not the user's.</summary>
    public bool IsOverridden(string key) => workspaceValues.ContainsKey(key);

    /// <summary>Every setting at what it comes as, by key.</summary>
    public static IReadOnlyDictionary<string, IRuntimeValue> Usual => usual ??= new HexSettings(null).EditorValues();

    // Whether a value is what a setting comes as, by how it's written
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

    /// <summary>What the user has a setting at, whatever the folder says, what it comes as when the user never changed it.</summary>
    public IRuntimeValue? UserValue(string key) =>
        IsOverridden(key) ? userValues.GetValueOrDefault(key) ?? Usual.GetValueOrDefault(key) : EditorValues().GetValueOrDefault(key);

    /// <summary>
    /// The user's own value of a setting the folder covers, kept for the user's file while the folder's stays in force.
    /// Null is the user's back at what it comes as. Saving is the caller's.
    /// </summary>
    public void SetUser(string key, IRuntimeValue? value)
    {
        if (value is null)
            userValues.Remove(key);
        else
            userValues[key] = value;
    }

    /// <summary>
    /// The folder's settings read from a folder, null for none, the last folder's let go of first. Says what went wrong
    /// when its file doesn't read, null when it does or isn't there. One that doesn't read changes nothing when it's the
    /// file that's in force already, a new folder's starts with nothing of its own.
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
            if (!IsWorkspaceKey(key))
                continue;

            if (now.TryGetValue(key, out IRuntimeValue? users))
                userValues[key] = users;
            workspaceValues[key] = value;
        }

        Apply(workspaceValues);
        return null;
    }

    /// <summary>
    /// A setting set for the folder and written to its file, what the user had kept. The setting itself is the caller's
    /// to put in force, the way a row would, so whatever else it changes changes too. Says what went wrong, null when fine.
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
    /// A setting the folder's file sets taken out of it, the user's again. The user's value (UserValue, asked before this)
    /// is the caller's to put back in force. Says what went wrong, null when fine.
    /// </summary>
    public string? RemoveWorkspace(string key) => workspaceValues.Remove(key) ? SaveWorkspace() : null;

    /// <summary>
    /// The folder's file written, with what it sets and nothing else, made with its folder when it isn't there. Says
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
            editor.Note("tab_size: 2, the editor's settings this folder has its own way, over the user's");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(at)!);
            File.WriteAllText(at, SettingsSections.Write(at, (SettingsSections.EDITOR, editor)));
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
