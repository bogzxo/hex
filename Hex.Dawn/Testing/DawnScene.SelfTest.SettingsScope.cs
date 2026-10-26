using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

using Hex.Shell;

namespace Hex.Dawn;

// The self test of a folder's own settings. A folder of its own in the temp folder (never the repository) with a
// .hex\settings.hor saying tab_size 2 over the user's 4, the open files going by it and back when it's taken out from
// outside, the window's User | Workspace switch, a change on Workspace written to the folder's file and nowhere else, its
// reset, a change on User kept for the user while the folder's stays in force, the user's file keeping only what isn't
// what it comes as, and the palette's commands for both files.
internal sealed partial class DawnScene
{
    private void TestSettingsScope(HexPointerScript test)
    {
        string folder = string.Empty, folderFile = string.Empty, userFile = string.Empty, plain = string.Empty, later = string.Empty, userText = string.Empty;
        HexSettings? was = null;
        HexWorkspace? workspaceWas = null;
        HexDocument? before = null;
        int every = 0;

        HexDocument? Opened(string file) => documents.FirstOrDefault(open => open.Path is { } path && PlatformPaths.Same(path, file));
        Dictionary<string, IRuntimeValue> Section(string file) =>
            SettingsSections.Read(file) is { } all ? SettingsSections.Section(all, SettingsSections.EDITOR) : [];
        static string Editor(string body) => "let settings = {\n    editor: {\n" + body + "    }\n};\n";

        // The user's settings a file of the test's own, everything as it comes, and a folder of its own with tab_size 2
        test.Run(() =>
        {
            CloseSettings();
            before = document;
            was = settings;
            workspaceWas = explorer.workspace;

            folder = Path.Combine(Path.GetTempPath(), $"hex_selftest_scope_{System.Environment.ProcessId}");
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(Path.Combine(folder, HexSettings.WORKSPACE_FOLDER));
            folderFile = HexSettings.WorkspacePathOf(folder);
            File.WriteAllText(folderFile, Editor("        tab_size: 2,\n"));
            plain = Path.Combine(folder, "plain.txt");
            later = Path.Combine(folder, "later.txt");
            File.WriteAllText(plain, "alpha\nbeta\n");
            File.WriteAllText(later, "gamma\n");

            userFile = Path.Combine(testFolder, "selftest_scope_user.hor");
            if (File.Exists(userFile))
                File.Delete(userFile);
            settingsFile = userFile;
            settings = new HexSettings(userFile);
            settings.Save();
            ApplyReadSettings(was);
            Open(plain);
            explorer.OpenFolder(folder);
        });
        test.WaitFor(() => settings.TabSize == 2, 3.0f);
        test.Run(() => Open(later));
        test.Wait();

        test.Check("settings scope: the folder's .hex\\settings.hor tab_size 2 goes over the user's 4, the open file and one opened after indent with 2", () =>
            settings.TabSize == 2 && settings.IsOverridden("tab_size") && PlatformPaths.Same(settings.WorkspacePath ?? string.Empty, folderFile)
            && Opened(plain) is { Editor: { TabSize: 2, Document.Indent: "  " } } && Opened(later) is { Editor.TabSize: 2 }
            && !File.ReadAllText(userFile).Contains("tab_size", StringComparison.Ordinal));

        // Taken out of the folder's file by something else, and the user's back without a restart
        test.Run(() => File.WriteAllText(folderFile, Editor(string.Empty)));
        test.WaitFor(() => settings.TabSize == 4, 3.0f);
        test.Check("settings scope: tab_size taken out of the folder's file outside Hex puts the user's 4 back, live", () =>
            settings.TabSize == 4 && !settings.IsOverridden("tab_size") && Opened(plain) is { Editor: { TabSize: 4, Document.Indent: "    " } });

        // A folder's file that doesn't read says so the way the user's does, and nothing changes
        test.Run(() => File.WriteAllText(folderFile, Editor("        tab_size: = 2,\n")));
        test.WaitFor(() => BrokenCard(folderFile) is not null, 3.0f);
        test.Check("settings scope: a folder's file that doesn't read says so on a card and leaves the settings as they were", () =>
            BrokenCard(folderFile) is not null && settings.TabSize == 4);
        test.Run(() =>
        {
            foreach (UINotification card in toasts.Cards)
                card.Close();
            File.WriteAllText(folderFile, Editor("        tab_size: 2,\n"));
        });
        test.WaitFor(() => settings.TabSize == 2, 3.0f);

        /* The window's switch */

        test.Run(() =>
        {
            userText = File.ReadAllText(userFile);
            OpenSettings(null);
        });
        test.Wait();
        test.Check("settings scope: the window has a User | Workspace switch, on User, Workspace named after the folder", () =>
            SettingsPage is { } page && userScopeTab is { Selected: true } && workspaceScopeTab is { Selected: false, Enabled: true } tab
            && tab.Label.Contains(Path.GetFileName(folder), StringComparison.Ordinal) && !InWorkspaceScope
            && page.Settings.Any(setting => setting.Key == "editor_skin"));
        test.Run(() =>
        {
            every = SettingsPage!.Settings.Count;
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "settings_scope_user.png"));
        });
        test.Settle(0.3f);

        test.Click(() => workspaceScopeTab is { } tab ? chrome.ToWorld(tab.Bounds.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings scope: clicking Workspace shows only the rows a folder can have", () =>
            InWorkspaceScope && workspaceScopeTab is { Selected: true } && SettingsPage is { } page && page.Settings.Count < every
            && page.Settings.All(setting => HexSettings.IsWorkspaceKey(setting.Key)) && page.IsRowShown("tab_size") && !page.IsRowShown("editor_skin"));
        test.Check("scope: the folder's tab says only what a folder can have is shown", () =>
            ScopeLineShown is { } line && line.Contains(Said("settings_page.workspace_only"), StringComparison.Ordinal));
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "settings_scope_workspace.png"));
        });
        test.Settle(0.3f);
        test.Check("settings scope: on Workspace tab_size shows changed, the folder's file sets it, and insert_spaces doesn't", () =>
            SettingsPage is { } page && page.IsModified!("tab_size") && !page.IsModified!("insert_spaces") && page.LineOf("tab_size") == "tab_size: 2");

        test.Run(() => SettingsPage!.Enter("tab_size", new NumberValue(3.0f)));
        test.Check("settings scope: tab_size 3 on Workspace is written to the folder's file alone, the user's file left as it was", () =>
            settings.TabSize == 3 && Opened(plain) is { Editor.TabSize: 3 } && SettingsPage!.IsModified!("tab_size")
            && Section(folderFile) is { Count: 1 } folders && folders["tab_size"] is NumberValue { Value: 3.0f }
            && File.ReadAllText(userFile) == userText);

        test.Run(() => SettingsPage!.Reset("tab_size"));
        test.Check("settings scope: its reset takes tab_size out of the folder's file and the user's 4 is back", () =>
            settings.TabSize == 4 && !settings.IsOverridden("tab_size") && !Section(folderFile).ContainsKey("tab_size")
            && !SettingsPage!.IsModified!("tab_size") && Opened(plain) is { Editor.TabSize: 4 });

        test.Run(() => SettingsPage!.Enter("tab_size", new NumberValue(2.0f)));
        test.Click(() => userScopeTab is { } tab ? chrome.ToWorld(tab.Bounds.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings scope: back on User every row is there again", () =>
            !InWorkspaceScope && userScopeTab is { Selected: true } && SettingsPage is { } page && page.Settings.Count == every && page.IsRowShown("editor_skin"));

        // On User a setting the folder covers is the user's for other folders, the folder's stays in force
        test.Run(() => SettingsPage!.Enter("tab_size", new NumberValue(8.0f)));
        test.Check("settings scope: tab_size 8 on User while the folder sets 2 goes in the user's file, 2 stays in force, and it's said", () =>
            settings.TabSize == 2 && Opened(plain) is { Editor.TabSize: 2 } && Section(userFile).GetValueOrDefault("tab_size") is NumberValue { Value: 8.0f }
            && Section(folderFile).GetValueOrDefault("tab_size") is NumberValue { Value: 2.0f } && SettingsPage!.LineOf("tab_size") == "tab_size: 8"
            && messageItem.Text.Contains(Path.GetFileName(folder), StringComparison.Ordinal));
        // A frame drawn with the note in it before the picture, and the picture written before going on. It was taken
        // before the frame landed and written empty
        test.Wait();
        test.Settle(0.3f);
        test.Run(() => Shot("settings-scope-note"));
        test.WaitFor(() => ShotWritten("settings-scope-note"), 3.0f);
        test.Check("settings scope: the note's picture is written with something in it when pictures are asked for", () => ShotWritten("settings-scope-note"));
        test.Check("scope: user row says set by workspace", () =>
            SettingsPage!.NoteShown("tab_size") == Said("settings_page.set_by_workspace") && SettingsPage.NoteShown("insert_spaces") is null);

        /* The user's file keeps only what isn't what it comes as */

        test.Check("settings scope: a fresh user file keeps only what differs from what it comes as, wrap_code true among it, and reads back the same", () =>
        {
            string file = Path.Combine(testFolder, "selftest_scope_sparse.hor");
            File.Delete(file);
            var written = new HexSettings(file) { WrapCode = true };
            if (written.Save() is not null)
                return false;

            var kept = Section(file);
            var read = new HexSettings(file);
            File.Delete(file);
            return kept.Count == 1 && kept["wrap_code"] is BooleanValue { Value: true }
                && read is { WrapCode: true, TabSize: 4, Minimap: true, EditorSkin: "flat" }
                && read.EditorValues().All(pair => SettingValues.Same(pair.Value, written.EditorValues()[pair.Key]));
        });

        /* The palette */

        test.Run(() => CloseSettings());
        test.Check("settings scope: File has one Settings..., no second way to the same window, and Open Workspace Settings File", () =>
            menu.Menus.First(bar => bar.Title == "File").Items.ToList() is var items
            && items.Count(item => item.Label == Said("settings_window.open")) == 1 && Said("settings_window.open") == "Settings..."
            && !items.Any(item => item.Label == Said("settings_window.open_ui"))
            && items.Any(item => item.Label == Said("settings_scope.open_workspace_file")));

        // Opened with no file there yet, it's made, an empty editor section, and opened in a tab
        test.Run(() => File.Delete(folderFile));
        test.WaitFor(() => settings.TabSize == 8, 3.0f);
        Pick("File", () => Said("settings_scope.open_workspace_file"));
        test.Wait();
        test.Check("settings scope: Open Workspace Settings File makes the folder's file when it has none and opens it in a tab", () =>
            File.Exists(folderFile) && document.Path is { } path && PlatformPaths.Same(path, folderFile)
            && SettingsSections.Read(folderFile) is { } all && all.ContainsKey(SettingsSections.EDITOR) && Section(folderFile).Count == 0);

        // Everything as it was, the folder gone with its .hex
        test.Run(() =>
        {
            foreach (string file in new[] { folderFile, plain, later })
            {
                if (Opened(file) is { } open)
                {
                    Show(open);
                    CloseNow();
                }
            }

            foreach (UINotification card in toasts.Cards)
                card.Close();
            explorer.workspace = workspaceWas ?? new HexWorkspace();
            treeDirty = true;
            settingsFile = null;
            HexSettings used = settings;
            settings = was!;
            ReloadWorkspaceSettings();
            ApplyReadSettings(used);
            if (File.Exists(userFile))
                File.Delete(userFile);
            Directory.Delete(folder, recursive: true);
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
        test.Check("settings scope: the test's folder is gone and no .hex was made in the repository", () =>
            !Directory.Exists(folder) && !Directory.Exists(Path.Combine(RepositoryRoot(), HexSettings.WORKSPACE_FOLDER))
            && !Directory.Exists(Path.Combine(testFolder, HexSettings.WORKSPACE_FOLDER)));
    }

    // Whether a picture asked for with HEX_SELFTEST_SHOTS has been written with something in it, true while none are asked for
    private static bool ShotWritten(string name)
    {
        string? folder = System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS");
        if (string.IsNullOrEmpty(folder))
            folder = System.Environment.GetEnvironmentVariable("DAWN_SELFTEST_SHOTS");
        if (string.IsNullOrEmpty(folder))
            return true;

        var file = new FileInfo(Path.Combine(folder, name + ".png"));
        return file.Exists && file.Length > 0;
    }
}
