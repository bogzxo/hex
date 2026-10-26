using System.Globalization;

using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// What's wrong with a settings file written by hand, the user's and the folder's .hex\settings.hor, said where it stays
// in sight. A file that doesn't read at its line, a key Hex hasn't got (with the one it probably meant), VS Code's name
// for a setting written where Hex's goes (with Hex's), a key the folder
// can't have its own way, a value of the wrong kind, a number past its ends and a choice that isn't one of them, each at
// its key's line. Underlined in the file while it's open, in Problems either way, worked out again whenever either file
// is read again or saved, so they go the moment the file is right.
internal sealed partial class DawnScene
{
    // What's wrong with each settings file, by its full path, only the files with something wrong
    private readonly Dictionary<string, List<HexProblem>> settingsProblems = new(PlatformPaths.Comparer);

    /// <summary>
    /// The user's settings file and the folder's looked over again, what they had before let go of. The files open in a
    /// tab are looked over as they are there, and underlined again.
    /// </summary>
    private void CheckSettingsFiles()
    {
        var before = new HashSet<string>(settingsProblems.Keys, PlatformPaths.Comparer);
        settingsProblems.Clear();

        if (settingsWatched is { } user)
            CheckSettingsFile(user, folder: false);
        if (settings.WorkspacePath is { } folders)
            CheckSettingsFile(Path.GetFullPath(folders), folder: true);

        before.UnionWith(settingsProblems.Keys);
        foreach (HexDocument open in documents)
        {
            if (open is { IsText: true, Path: { } path } && before.Contains(Path.GetFullPath(path)))
                ShowDiagnostics(open);
        }

        problemsDirty = true;
    }

    // One file looked over, its text in its tab when it's open there and on the disk otherwise
    private void CheckSettingsFile(string path, bool folder)
    {
        string full = Path.GetFullPath(path);
        string? text = OpenDocument(full) is { IsText: true } open ? open.Editor.Text : ReadSettingsText(full);
        if (text is null)
            return;

        List<HexProblem> found = SettingsProblemsIn(full, text.ReplaceLineEndings("\n"), folder);
        if (found.Count > 0)
            settingsProblems[full] = found;
    }

    /// <summary>What's wrong with the text of a settings file, the folder's when <paramref name="folder"/>, each at its line.</summary>
    internal static List<HexProblem> SettingsProblemsIn(string path, string text, bool folder)
    {
        var found = new List<HexProblem>();
        Dictionary<string, IRuntimeValue> all;
        try
        {
            all = SettingsSections.ReadText(text);
        }
        catch (Exception e)
        {
            var (line, column) = e is SettingsReadException { Line: > 0 } placed ? (placed.Line - 1, Math.Max(0, placed.Column - 1)) : (0, 0);
            found.Add(new HexProblem(path, line, column, Said("settings_problems.doesnt_read", ("error", e.Message)), false));
            return found;
        }

        void Add(string section, string key, string message)
        {
            // A VS Code id is written in quotes, which the usual way of finding a key's line doesn't know
            var (line, column) = VsCodeSettings.HexKeyOf(key) is null ? LineOfSetting(text, section, key) : LineOfQuotedKey(text, key);
            found.Add(new HexProblem(path, line, column, message, true));
        }

        // A section the folder's file can't have, the terminal's and the extensions' are only ever the user's
        if (SettingsSections.IsSectioned(all))
        {
            foreach (var (name, value) in all)
            {
                if (value is not ObjectValue)
                {
                    // A VS Code id at the top, the way VS Code's own file has them
                    if (VsCodeName(name) is { } vscode)
                        Add("settings", name, vscode);
                    continue;
                }

                if (name is not (SettingsSections.EDITOR or SettingsSections.TERMINAL or SettingsSections.EXTENSIONS))
                    Add("settings", name, Unknown("settings", name, [SettingsSections.EDITOR, SettingsSections.TERMINAL, SettingsSections.EXTENSIONS]));
                else if (folder && name != SettingsSections.EDITOR)
                    Add("settings", name, Said("settings_problems.user_only", ("key", name)));
            }
        }

        foreach (var (key, value) in SettingsSections.Section(all, SettingsSections.EDITOR))
        {
            if (EditorSettingProblem(key, value, folder) is { } problem)
                Add(SettingsSections.EDITOR, key, problem);
        }

        if (!folder)
        {
            foreach (var (key, value) in SettingsSections.Section(all, SettingsSections.TERMINAL))
            {
                if (TerminalSettingProblem(key, value) is { } problem)
                    Add(SettingsSections.TERMINAL, key, problem);
            }
        }

        return found;
    }

    // The keys of the editor's section, everything Hex writes there and every row of the catalog
    private static HashSet<string>? editorKeys;

    private static HashSet<string> EditorKeys => editorKeys ??= new HashSet<string>(
        HexSettings.Usual.Keys.Concat(EditorSettingsCatalog.Settings.Select(row => row.Key)).Concat(EditorSettingsCatalog.Text.Select(row => row.Key))
            .Append("extension_settings"), StringComparer.Ordinal);

    // The editor's settings that are an object of texts by text, a row of pairs
    private static readonly HashSet<string> PairsKeys = [.. EditorSettingsCatalog.Settings.Concat(EditorSettingsCatalog.Text).Where(row => row.Kind == UIFormKind.Pairs).Select(row => row.Key)];

    /// <summary>
    /// The ends HexSettings clamps a number to as it reads the file (HexSettings.Apply), by key. A number of the editor's
    /// section not here is taken as it is (snap, the panels' sizes), so it's never said to be past an end. Kept in step
    /// with Apply by the settings self test, which reads a file past every end and sees what it was clamped to.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (float Least, float Most)> ClampEnds = new Dictionary<string, (float, float)>(StringComparer.Ordinal)
    {
        ["code_zoom"] = (0.5f, 3.0f),
        ["terminal_backdrop_scale"] = (0.05f, 1.5f),
        ["terminal_backdrop_across"] = (-0.5f, 1.0f),
        ["terminal_backdrop_up"] = (-0.5f, 1.0f),
        ["backdrop_wrap_margin"] = (0.0f, 80.0f),
        ["wrap_flow"] = (0.0f, 60.0f),
        ["git_autofetch_interval"] = (1.0f, 60.0f),
        ["git_subject_length"] = (20.0f, 100.0f),
        ["git_line_length"] = (40.0f, 120.0f),
        ["glass_blur"] = (0.0f, 64.0f),
        ["glass_clarity"] = (0.0f, 1.0f),
        ["glass_rim"] = (0.0f, 1.0f),
        ["glass_bend"] = (0.0f, 24.0f),
        ["ui_animation"] = (0.0f, 3.0f),
        ["caret_glide"] = (0.0f, 0.3f),
        ["tab_size"] = (1.0f, 16.0f),
        ["auto_save_delay"] = (0.25f, 60.0f),
        ["backdrop_scale"] = (0.05f, 1.5f),
        ["backdrop_across"] = (-0.5f, 1.0f),
        ["backdrop_up"] = (-0.5f, 1.0f),
        ["backdrop_opacity"] = (0.0f, 1.0f)
    };

    // What's wrong with one key of the editor's section, null for nothing
    private static string? EditorSettingProblem(string key, IRuntimeValue value, bool folder)
    {
        if (VsCodeName(key) is { } vscode)
            return vscode;

        if (!EditorKeys.Contains(key))
            return Unknown(SettingsSections.EDITOR, key, EditorKeys);

        if (folder && !HexSettings.IsWorkspaceKey(key))
            return Said("settings_problems.user_only", ("key", key));

        IRuntimeValue? usual = HexSettings.Usual.GetValueOrDefault(key) ?? EditorSettingsCatalog.Usual.GetValueOrDefault(key);
        if (usual is not null && KindOf(usual) is { } wants && KindOf(value) != wants)
            return Said($"settings_problems.wants_{wants}", ("key", key));

        // An object of texts (files_associations), a pattern whose language isn't a text is flagged, it'd be dropped
        if (value is ObjectValue { Properties: { } pairs } && PairsKeys.Contains(key))
        {
            foreach (var (pattern, language) in pairs)
            {
                if (language is not StringValue)
                    return Said("settings_window.pairs_want_text", ("key", key), ("pattern", pattern));
            }
        }

        // A number past the ends HexSettings clamps it to as the file is read, exactly then and never otherwise. The row's
        // own ends are where its slider stops, a few of those are short of what the file can have (glass_blur's 48 of 64)
        if (value is NumberValue number && ClampEnds.TryGetValue(key, out var ends))
            return Clamped(key, number.Value, ends.Least, ends.Most);

        UISetting? row = Array.Find(EditorSettingsCatalog.Settings, setting => setting.Key == key) ?? Array.Find(EditorSettingsCatalog.Text, setting => setting.Key == key);
        if (row is null)
            return null;

        // The font empty is the skin's own, it's not in the list of the fonts there are
        if (row is { Kind: UIFormKind.Choice, Choices: { } choices } && value is StringValue chosen && !(key == "code_font" && chosen.Value.Length == 0))
            return NotAChoice(key, chosen.Value, choices);

        return null;
    }

    // What's wrong with one key of the terminal's section, null for nothing
    private static string? TerminalSettingProblem(string key, IRuntimeValue value)
    {
        if (VsCodeName(key) is { } vscode)
            return vscode;

        if (Array.Find(TerminalSettings.Described, setting => setting.Key == key) is not { } info)
            return Unknown(SettingsSections.TERMINAL, key, TerminalSettings.Described.Select(setting => setting.Key));

        string? wants = info.Kind switch
        {
            "number" or "whole" => "number",
            "flag" => "flag",
            "text" or "choice" => "text",
            "list" => "list",
            _ => null
        };
        if (wants is not null && KindOf(value) != wants)
            return Said($"settings_problems.wants_{wants}", ("key", key));

        if (value is NumberValue number)
            return Clamped(key, number.Value, info.Least, info.Most);
        if (info.Choices is { } choices && value is StringValue chosen)
            return NotAChoice(key, chosen.Value, choices);
        return null;
    }

    // What kind of value something is by how the problems say it, null for one they don't name
    private static string? KindOf(IRuntimeValue value) => value switch
    {
        BooleanValue => "flag",
        NumberValue => "number",
        StringValue => "text",
        ListValue => "list",
        ObjectValue => "object",
        _ => null
    };

    // A key that's VS Code's name for one of Hex's, said with Hex's name, null for any other key
    private static string? VsCodeName(string key) =>
        VsCodeSettings.HexKeyOf(key) is { } hex ? Said("settings_import.vscode_name", ("id", key), ("key", hex)) : null;

    // The first line that sets a key written in quotes, the way a VS Code id is, the top when none does
    private static (int Line, int Column) LineOfQuotedKey(string text, string key)
    {
        string quoted = $"\"{key}\"";
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(quoted, StringComparison.Ordinal))
                return (i, lines[i].Length - trimmed.Length);
        }

        return (0, 0);
    }

    // A number past its ends, what it's clamped to said, null for one inside them or one that has none
    private static string? Clamped(string key, float value, float least, float most)
    {
        if (most <= least || (value >= least && value <= most))
            return null;

        static string Written(float number) => number.ToString("0.###", CultureInfo.InvariantCulture);
        return Said("settings_problems.clamped", ("key", key), ("least", Written(least)), ("most", Written(most)), ("value", Written(value)),
            ("to", Written(Math.Clamp(value, least, most))));
    }

    private static string? NotAChoice(string key, string value, string[] choices) =>
        choices.Contains(value, StringComparer.Ordinal) ? null
            : Said("settings_problems.not_a_choice", ("key", key), ("value", value), ("choices", string.Join(", ", choices)));

    // A key a section hasn't got, with the one it was most likely meant to be when one is near enough
    private static string Unknown(string section, string key, IEnumerable<string> keys)
    {
        string? near = null;
        int best = 3;
        foreach (string other in keys)
        {
            int distance = EditDistance(key, other, best);
            if (distance < best)
                (near, best) = (other, distance);
        }

        return near is null
            ? Said("settings_problems.unknown", ("section", section), ("key", key))
            : Said("settings_problems.did_you_mean", ("section", section), ("key", key), ("near", near));
    }

    /// <summary>How many letters have to change, go or come for one word to be the other, given up on past <paramref name="most"/>.</summary>
    internal static int EditDistance(string a, string b, int most = int.MaxValue)
    {
        if (Math.Abs(a.Length - b.Length) >= most)
            return most;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int smallest = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int change = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(change, Math.Min(previous[j] + 1, current[j - 1] + 1));
                smallest = Math.Min(smallest, current[j]);
            }

            if (smallest >= most)
                return most;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>What's wrong with a settings file that's open, underlined in its code. A file that doesn't read says so itself.</summary>
    private IReadOnlyList<UICodeDiagnostic>? SettingsDiagnosticsOf(HexDocument of)
    {
        if (settingsProblems.Count == 0 || of.Path is not { } path || !settingsProblems.TryGetValue(Path.GetFullPath(path), out List<HexProblem>? found))
            return null;

        bool readsItself = of.Editor.SyntaxErrors.Count > 0;
        var said = new List<UICodeDiagnostic>(found.Count);
        foreach (HexProblem problem in found)
        {
            if (!problem.Warning && readsItself)
                continue;
            said.Add(new UICodeDiagnostic(problem.Line, problem.Column, WordLength(of.Editor.Text, problem.Line, problem.Column), problem.Text, problem.Warning));
        }

        return said;
    }

    /// <summary>What's wrong with the settings files, for the problems panel, the open ones' are in their own diagnostics.</summary>
    private IEnumerable<HexProblem> SettingsProblems() => settingsProblems.Values.SelectMany(found => found);
}
