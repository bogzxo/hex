using System.Numerics;
using System.Text.Json;

using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// Somebody's VS Code settings brought over in one go, the user's settings.json into Hex's user settings and a folder's
// .vscode\settings.json into the folder's own. What's going to change is shown first, a row each with a switch, what Hex
// hasn't got listed dim under them. Each is set the way its row in the settings window would set it, through the same
// sources, so whatever a row does when it changes (the open files, the folder's file) happens here too.
internal sealed partial class DawnScene
{
    // The most of the preview's list shown before it scrolls
    private const float IMPORT_LIST_HEIGHT = 340.0f;

    /// <summary>
    /// One setting of the file as the preview has it, the source that sets it and what the row says. One that isn't
    /// settable is a dim row saying why, Hex hasn't got it, can't take its value or it's only ever the user's.
    /// </summary>
    private sealed record ImportRow(VsCodeSetting Setting, ISettingsSource? Source, string Text, bool Settable);

    /// <summary>What an import did, how many were set and the ids of the ones Hex left.</summary>
    internal sealed record VsCodeImported(int Set, List<string> Left);

    /// <summary>The user's VS Code settings, Code's, then Insiders', then VSCodium's, previewed to import.</summary>
    private void ImportVsCodeSettings()
    {
        if (VsCodeSettings.UserFile() is not { } path)
        {
            Say(Said("settings_import.none_found"), error: true);
            return;
        }

        Import(path, all: false);
    }

    /// <summary>The folder's .vscode\settings.json, previewed to import into the folder's own settings.</summary>
    private void ImportVsCodeWorkspaceSettings()
    {
        if (WorkspaceSettingsFolder() is not { } folder)
        {
            Say(Said("settings_scope.no_folder"), error: true);
            return;
        }

        string path = VsCodeSettings.WorkspaceFile(folder);
        if (!File.Exists(path))
        {
            Say(Said("settings_import.no_workspace_file", ("folder", FolderName(folder))), error: true);
            return;
        }

        Import(path, all: false, workspace: true);
    }

    /// <summary>
    /// A VS Code settings.json read and shown to import, or with <paramref name="all"/> every setting Hex can take set there
    /// and then (the self test's way in). Into the folder's settings when <paramref name="workspace"/>. What was set when
    /// it was, null for a file that doesn't read or one that's only been shown.
    /// </summary>
    internal VsCodeImported? Import(string path, bool all, bool workspace = false)
    {
        Dictionary<string, JsonElement> read;
        try
        {
            read = VsCodeSettings.Read(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Say(Said("settings_import.doesnt_read", ("path", path), ("error", e.Message)), error: true);
            return null;
        }

        List<ImportRow> rows = ImportRows(read, workspace);
        if (all)
            return ApplyImport(rows, [.. rows.Where(row => row.Settable)], workspace);

        ShowImportPreview(path, rows, workspace);
        return null;
    }

    // The sources the settings window has that a VS Code setting can land in, the editor's, the text's and the terminal's
    private List<ISettingsSource> ImportSources()
    {
        var sources = new List<ISettingsSource> { new EditorSource(this) };
        sources.AddRange(settingSources.Select(added => added.Source()));
        sources.Add(new TerminalSettingsSource(terminals, EditorSettingsCatalog.THEME));
        return sources;
    }

    // Every setting of the file as a row, the ones Hex sets first in the order the file had them, then the rest
    private List<ImportRow> ImportRows(Dictionary<string, JsonElement> read, bool workspace)
    {
        string[] fonts = Array.Find(EditorSettingsCatalog.Settings, row => row.Key == "code_font")?.Choices ?? [];
        List<ISettingsSource> sources = ImportSources();
        var rows = new List<ImportRow>();
        foreach (VsCodeSetting setting in VsCodeSettings.Plan(read, fonts))
        {
            // The first source that has the key sets it, the same one its row in the window is
            ISettingsSource? source = setting.Key is { } key ? sources.Find(each => each.Settings.Any(row => row.Key == key)) : null;
            UISetting? row = source?.Settings.FirstOrDefault(each => each.Key == setting.Key);
            string id = UIRichText.Plain(setting.Id), written = UIRichText.Plain(setting.Written);

            if (source is null || row is null || setting.Key is null)
                rows.Add(new ImportRow(setting, null, Said("settings_import.not_in_hex", ("id", id), ("value", written)), false));
            else if (setting.Value is not { } value)
                rows.Add(new ImportRow(setting, null, Said("settings_import.cant_take", ("id", id), ("value", written), ("title", row.Title)), false));
            else if (workspace && !HexSettings.IsWorkspaceKey(setting.Key))
                rows.Add(new ImportRow(setting, null, Said("settings_import.user_only", ("id", id), ("title", row.Title)), false));
            else
                rows.Add(new ImportRow(setting, source, Said("settings_import.row", ("id", id), ("value", written), ("title", row.Title),
                    ("hex", UIRichText.Plain(VsCodeSettings.Shown(value)))), true));
        }

        return [.. rows.OrderBy(row => row.Settable ? 0 : 1)];
    }

    // The preview, a switch for each setting Hex will set and the rest dim under them, the button counting what's ticked
    private void ShowImportPreview(string path, List<ImportRow> rows, bool workspace)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        var toggles = new List<(ToggleButton Toggle, ImportRow Row)>();
        List<ImportRow> Ticked() => [.. toggles.Where(pair => pair.Toggle.State).Select(pair => pair.Row)];

        UIDialog dialog = UIDialog.Show(chrome, Said(workspace ? "settings_import.title_workspace" : "settings_import.title"),
            Said("settings_import.from", ("path", UIRichText.Plain(path))),
            new DialogChoice(ImportLabel(rows.Count(row => row.Settable)), () => ApplyImport(rows, Ticked(), workspace)),
            new DialogChoice(Said("settings_import.leave")));

        var list = new StackPanel { Anchor = Horizon.Rendering.Origin.TopLeft, Spacing = 6.0f };
        foreach (ImportRow row in rows)
        {
            if (row.Settable)
            {
                var toggle = list.Add(new ToggleButton(row.Text, startingState: true));
                toggle.OnPressed = () => dialog.Buttons[0].Label = ImportLabel(Ticked().Count);
                toggles.Add((toggle, row));
            }
            else
            {
                list.Add(new Label($"[color:dim]{row.Text}[/color]") { Anchor = Horizon.Rendering.Origin.Left, Align = Horizon.Rendering.Origin.Left });
            }
        }

        if (rows.Count == 0)
            list.Add(new Label($"[color:dim]{Said("settings_import.empty")}[/color]") { Anchor = Horizon.Rendering.Origin.Left, Align = Horizon.Rendering.Origin.Left });

        // A long file scrolls, a short one is as tall as it is
        float tall = MathF.Min(IMPORT_LIST_HEIGHT, MathF.Max(1, rows.Count) * 30.0f);
        var scroll = dialog.Body.Add(new ScrollPanel { Size = new Vector2(560.0f, tall), Anchor = Horizon.Rendering.Origin.Left });
        scroll.Add(list);
        textDialog = dialog;
    }

    private static string ImportLabel(int count) =>
        count == 1 ? Said("settings_import.import_one") : Said("settings_import.import", ("count", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // The settings ticked set, each through its source the way its row would. Into the folder's file when it's the
    // folder's, the window's switch put over there for as long as it takes
    private VsCodeImported? ApplyImport(List<ImportRow> rows, List<ImportRow> ticked, bool workspace)
    {
        if (workspace && !OnWorkspaceSettings())
            return null;

        bool scopeWas = workspaceScope;
        workspaceScope = workspace;
        try
        {
            foreach (ImportRow row in ticked)
            {
                row.Source!.Set(row.Setting.Key!, row.Setting.Value!);
                SettingsPage?.Refresh(row.Setting.Key!);
            }
        }
        finally
        {
            workspaceScope = scopeWas;
        }

        List<string> left = [.. rows.Where(row => !row.Settable).Select(row => row.Setting.Id)];
        string count = ticked.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string said = left.Count == 0 ? Said("settings_import.imported", ("count", count))
            : Said("settings_import.imported_left", ("count", count), ("left", left.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Notify(said, actions: (Said("settings_import.show_modified"), ShowModifiedSettings));
        return new VsCodeImported(ticked.Count, left);
    }

    // The folder's settings file the one in force, the first folder's, false with nothing open to have one
    private bool OnWorkspaceSettings()
    {
        if (WorkspaceSettingsFolder() is not { } folder)
        {
            Say(Said("settings_scope.no_folder"), error: true);
            return false;
        }

        if (settings.WorkspacePath is not { } path || !PlatformPaths.Same(path, HexSettings.WorkspacePathOf(folder)))
            ReloadWorkspaceSettings();
        return settings.WorkspacePath is not null;
    }

    /// <summary>The settings window searched for what somebody changed, @modified.</summary>
    private void ShowModifiedSettings()
    {
        OpenSettings(null);
        SettingsPage?.AddToSearch(Horizon.UI.Components.SettingsPage.MODIFIED);
    }
}
