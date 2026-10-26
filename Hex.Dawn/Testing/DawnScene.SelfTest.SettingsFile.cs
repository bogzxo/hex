using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

using Hex.Shell;

namespace Hex.Dawn;

// The self test of the settings window and the settings file as one thing. The buttons on the window's title bar, the
// { } opening the file at the row the keyboard was on and Ctrl+, in the file going back to the setting on the caret's
// line, the file read again and put in force as it's saved, one that doesn't read saying where and changing nothing, a
// change from outside Hex taken in too, the theme row's preview, no dot on Panels nobody changed, and only what isn't
// what it comes as written to the file. A file written by hand read without its settings object, what's wrong with one
// in Problems at its lines, and the palette's Preferences commands. A folder's own settings over it are SettingsScope's.
internal sealed partial class DawnScene
{
    private void TestSettingsFile(HexPointerScript test)
    {
        string file = string.Empty;
        HexSettings? was = null, before = null;
        HexDocument? shown = null;

        bool OnFile() => document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file);
        string CaretLine() => code.Document.LineAt(code.Document.Caret.Line);

        // A file of its own the editor's settings are kept in for the length of this, wrap, the minimap and blame off in
        // it. Only what isn't what it comes as is written, so the minimap and blame are and wrap isn't
        test.Run(() =>
        {
            CloseSettings();
            shown = document;
            was = settings;
            file = Path.Combine(testFolder, "selftest_settings_file.hor");
            if (File.Exists(file))
                File.Delete(file);
            settingsFile = file;
            settings = new HexSettings(file) { WrapCode = false, Minimap = false, GitBlame = false };
            settings.Save();
            ApplyReadSettings(was);
            WatchSettingsFile();
        });
        test.Wait();

        test.Check("settings file: the file Hex writes has only what isn't what it comes as, the minimap and blame and not wrap or the tab size", () =>
            File.ReadAllText(file) is var text && text.Contains("minimap: false", StringComparison.Ordinal) && text.Contains("git_blame: false", StringComparison.Ordinal)
            && !text.Contains("wrap_code", StringComparison.Ordinal) && !text.Contains("tab_size", StringComparison.Ordinal));

        /* The window's title bar and what's in it */

        test.Run(() => OpenSettings(null));
        test.Wait();
        test.Check("settings file: the window's title bar has the file's { } and the keyboard's button left of its x", () =>
            SettingsWindow is { TitleActions: 2 } window && window.TitleActionIcon(0) == "json" && window.TitleActionIcon(1) == "keyboard"
            && window.TitleActionBounds(1).Max.X <= window.CloseBounds.Min.X + 0.5f
            && window.TitleActionTooltip(0) == Said("settings_window.open_file"));

        // The pointer resting on the { } puts its tooltip up, for a look with HEX_SELFTEST_SHOTS set
        test.Hover(() => SettingsWindow is { } over ? chrome.ToWorld(over.TitleActionBounds(0).Center) : test.Pointer.Position);
        test.Settle(1.2f);
        test.Check("settings file: resting on the { } says what it does", () => SettingsWindow is { } window && window.Tooltip == Said("settings_window.open_file"));
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "settings_title_actions.png"));
        });
        test.Settle(0.3f);

        test.Check("settings file: Panels has no dot when nobody picked a size", () =>
            SettingsPage is { } page && !page.IsModified!("size") && !page.SectionsChanged.Contains(PANELS));

        test.Run(() => SettingsPage!.Find("keybinding"));
        test.Wait();
        test.Check("settings file: searching keybinding finds the section that goes to the keyboard shortcuts", () =>
            SettingsPage!.SectionsShown.Contains(KEYS_SECTION));
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Wait();

        // The theme row's preview is Color Theme's list, the one that shows each as it goes past and puts the old one back
        string skinWas = string.Empty, themeWas = string.Empty;
        test.Run(() =>
        {
            (skinWas, themeWas) = (settings.EditorSkin, settings.EditorTheme);
            SettingsPage!.OnButton!("editor_skin", 0);
        });
        test.Wait();
        test.Check("settings file: the theme row's Preview themes opens Color Theme's list", () =>
            textDialog is { IsOpen: true } dialog && Inside<ListBox>(dialog.Body) is { } list && list.Items.Length == EditorSkins().Count);
        test.Run(() => textDialog?.Cancel());
        test.Wait();
        test.Check("settings file: and Escape out of it leaves the theme as it was", () =>
            textDialog is not { IsOpen: true } && settings.EditorSkin == skinWas && settings.EditorTheme == themeWas);

        /* The { } at a row, and Ctrl+, back */

        test.Run(() => SettingsPage!.Reveal("git_blame"));
        test.WaitFor(() => SettingsPage?.KeyWithKeyboard == "git_blame", 2.0f);
        test.Click(() => SettingsWindow is { } window ? chrome.ToWorld(window.TitleActionBounds(0).Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings file: the { } with git_blame's row on the keyboard opens the file with the caret on git_blame", () =>
            OnFile() && KeyOf(CaretLine()) == "git_blame" && SettingsPage is null);

        test.Run(() =>
        {
            code.GoTo(LineOfSetting(code.Text, SettingsSections.EDITOR, "minimap").Line, 4);
            OpenSettings();
        });
        test.WaitFor(() => SettingsPage?.KeyWithKeyboard == "minimap", 2.0f);
        test.Check("settings file: Ctrl+, with the caret on minimap's line opens the window at minimap", () =>
            SettingsPage is { } page && page.KeyWithKeyboard == "minimap" && page.IsRowShown("minimap"));

        /* Saved in Hex, read again */

        test.Run(() =>
        {
            code.Document.Replace(code.Text.Replace("minimap: false,", "minimap: false,\n        wrap_code: true,"));
            Save();
        });
        test.WaitFor(() => settings.WrapCode, 2.0f);
        test.Check("settings file: saving it with wrap_code true put in wraps the open files and the row shows it, no restart", () =>
            settings.WrapCode && documents.All(open => open.Editor.WordWrap)
            && SettingsPage?.LineOf("wrap_code") == "wrap_code: true" && SettingsPage.IsModified!("wrap_code"));

        // A file that doesn't read is said with where, and the settings stay, nothing dropped back to what it comes as
        test.Run(() =>
        {
            before = settings;
            code.Document.Replace(code.Text.Replace("minimap: false", "minimap: = false"));
            Save();
        });
        test.WaitFor(() => BrokenCard(file) is not null, 2.0f);
        test.Check("settings file: saving one that doesn't read says so with its line and leaves the settings as they were", () =>
            BrokenCard(file) is { } card && card.Text.Contains("line", StringComparison.Ordinal)
            && settings == before && settings.WrapCode && !settings.Minimap && documents.All(open => open.Editor.WordWrap));

        /* Changed outside Hex */

        test.Run(() =>
        {
            code.Document.Replace(code.Text.Replace("minimap: = false", "minimap: false"));
            Save();
            if (OnFile())
                Close();
            CloseSettings();
            File.WriteAllText(file, File.ReadAllText(file).Replace("minimap: false", "minimap: true"));
        });
        test.WaitFor(() => settings.Minimap, 3.0f);
        test.Check("settings file: a change written by anything else is taken in as well", () =>
            settings.Minimap && settings.WrapCode && documents.All(open => open.Editor.Minimap));

        TestSettingsPairsFile(test, () => file);
        TestSettingsFileProblems(test, () => file);
        TestSettingsTruthfulProblems(test);
        TestVsCodeImport(test, () => file);

        // Everything as it was, the test's own settings again and nothing watched
        test.Run(() =>
        {
            foreach (UINotification card in toasts.Cards)
                card.Close();
            StopWatchingSettings();
            settingsFile = null;
            HexSettings used = settings;
            settings = was!;
            ApplyReadSettings(used);
            if (shown is not null && documents.Contains(shown))
                Show(shown);
        });
        test.Wait();

        // A folder's own settings over these, see SettingsScope
        TestSettingsScope(test);
    }

    /// <summary>
    /// A layout on the canvas for the settings' part run on its own, what the designer's part leaves behind otherwise. The
    /// window is clicked where it's over the canvas, and the skins are looked at on a component of it.
    /// </summary>
    private void OnLayoutForSettings(HexPointerScript test)
    {
        test.Run(() =>
        {
            if (!document.IsText && box is { Module: not null } && document.Walk().Any())
                return;

            Open(savedFile);
            SwitchView(HexView.Design);
            box = document.Walk().Select(pair => pair.Component).LastOrDefault();
        });
        test.Wait();
    }

    /// <summary>
    /// A settings file written by hand, read without the settings object round it, one that declares nothing saying what it
    /// should, and what's wrong with one in Problems at its lines until it's put right, the folder's rules too. Then the
    /// palette's Preferences commands. The user's file is the test's own, watched.
    /// </summary>
    private void TestSettingsFileProblems(HexPointerScript test, Func<string> userFile)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"hex_selftest_problems_{System.Environment.ProcessId}");
        string folderFile = HexSettings.WorkspacePathOf(folder);
        HexWorkspace? workspaceWas = null;

        IEnumerable<HexProblem> ProblemsOf(string file) => problemList.Where(problem => PlatformPaths.Same(problem.Path, file));

        // The sections at the top without the object round them, the way somebody writes one from memory
        test.Check("file: top-level editor section reads", () =>
        {
            Directory.CreateDirectory(Path.Combine(folder, HexSettings.WORKSPACE_FOLDER));
            File.WriteAllText(folderFile, "let editor = { tab_size: 3 };\n");
            var read = new HexSettings(null);
            bool loose = read.LoadWorkspace(folder) is null && read.TabSize == 3;
            File.WriteAllText(folderFile, "editor: { tab_size: 5 }\n");
            bool inside = read.LoadWorkspace(folder) is null && read.TabSize == 5;
            bool wrapped = read.SaveWorkspace() is null && File.ReadAllText(folderFile).Contains("let settings = {", StringComparison.Ordinal);
            Directory.Delete(folder, recursive: true);
            return loose && inside && wrapped;
        });

        test.Check("file: broken file says the skeleton", () =>
        {
            string broken = Path.Combine(testFolder, "selftest_settings_skeleton.hor");
            File.WriteAllText(broken, "let colours = { dark: true };\n");
            bool said = SettingsSections.TryRead(broken, out string? error) is null && error is not null
                && error.Contains(Said("settings_problems.skeleton"), StringComparison.Ordinal);
            File.Delete(broken);

            // One that doesn't read is placed at the line HIDL said, from 0 as the editor counts
            return said && SettingsProblemsIn("x", "let settings = {\n    editor: {\n        minimap: = false\n    }\n};\n", folder: false) is [{ Line: 2, Warning: false }];
        });

        // A key spelt wrong, a number past its end and a word where a switch goes, each at its own line
        test.Run(() => File.WriteAllText(userFile(),
            "let settings = {\n    editor: {\n        tab_sise: 4,\n        tab_size: 99,\n        wrap_code: \"yes\"\n    }\n};\n"));
        test.WaitFor(() => ProblemsOf(userFile()).Count() == 3, 3.0f);
        test.Check("problems: unknown key, range, type", () =>
            ProblemsOf(userFile()).ToList() is { Count: 3 } found
            && found.Any(problem => problem.Line == 2 && problem.Text.Contains("did you mean tab_size", StringComparison.Ordinal))
            && found.Any(problem => problem.Line == 3 && problem.Text.Contains("clamped to 16", StringComparison.Ordinal))
            && found.Any(problem => problem.Line == 4 && problem.Text.Contains("wants on or off", StringComparison.Ordinal))
            && found.All(problem => problem.Warning));

        test.Run(() => File.WriteAllText(userFile(), "let settings = {\n    editor: {\n        tab_size: 8\n    }\n};\n"));
        test.WaitFor(() => settings.TabSize == 8 && !ProblemsOf(userFile()).Any(), 3.0f);
        test.Check("problems: and fixing the file clears them", () => settings.TabSize == 8 && !ProblemsOf(userFile()).Any());

        // The folder's file setting what only the user's can, the sparks are how Hex looks and not a project's
        test.Run(() =>
        {
            workspaceWas = explorer.workspace;
            Directory.CreateDirectory(Path.Combine(folder, HexSettings.WORKSPACE_FOLDER));
            File.WriteAllText(folderFile, "let settings = {\n    editor: {\n        tab_size: 2,\n        effect_sparks: true\n    }\n};\n");
            explorer.OpenFolder(folder);
        });
        test.WaitFor(() => ProblemsOf(folderFile).Any(), 3.0f);
        test.Check("problems: folder-only rule", () =>
            ProblemsOf(folderFile).ToList() is [{ Line: 3, Warning: true } only] && only.Text.Contains("effect_sparks", StringComparison.Ordinal)
            && only.Text == Said("settings_problems.user_only", ("key", "effect_sparks")) && settings.TabSize == 2);
        test.Run(() =>
        {
            explorer.workspace = workspaceWas ?? new HexWorkspace();
            treeDirty = true;
            ReloadWorkspaceSettings();
            Directory.Delete(folder, recursive: true);
        });
        test.WaitFor(() => !ProblemsOf(folderFile).Any(), 3.0f);
        test.Check("problems: the folder's go with the folder", () => !ProblemsOf(folderFile).Any() && (settings.WorkspacePath is not { } at || !PlatformPaths.Same(at, folderFile)));

        /* The palette's Preferences commands */

        bool minimapWas = false;
        (string Text, Action Picked) Command(string text) => AllCommands().First(command => command.Text == text);
        test.Run(() =>
        {
            minimapWas = settings.Minimap;
            OpenSettings(null);
        });
        test.Wait();
        test.Run(() => Command(Said("settings_palette.toggle", ("title", Said("settings_catalog.minimap_title")))).Picked());
        test.Wait();
        test.Check("palette: settings commands", () =>
            AllCommands() is var all && all.Any(command => command.Text == "Preferences: Toggle Minimap")
            && all.Any(command => command.Text == "Preferences: Open Settings (UI)") && all.Any(command => command.Text == "Preferences: Show Modified Settings")
            && all.Any(command => command.Text == "Preferences: Open Settings: " + EditorSettingsCatalog.GIT)
            && all.Any(command => command.Text == "Preferences: Toggle " + Said("settings_text.insert_spaces_title"))
            && settings.Minimap != minimapWas && documents.All(open => open.Editor.Minimap == settings.Minimap)
            && SettingsPage?.LineOf("minimap") == $"minimap: {(settings.Minimap ? "true" : "false")}");

        test.Run(() =>
        {
            Command("Preferences: Toggle Minimap").Picked();
            Command("Preferences: Show Modified Settings").Picked();
        });
        test.Wait();
        test.Check("palette: toggled back, and Show Modified Settings searches the window for what's changed", () =>
            settings.Minimap == minimapWas && SettingsPage is { } page && page.Search.Text.StartsWith(Horizon.UI.Components.SettingsPage.MODIFIED, StringComparison.Ordinal));
        test.Run(() => CloseSettings());
        test.Wait();
    }

    /// <summary>
    /// VS Code's settings brought over. A settings.json with comments and a comma after its last setting read, each id put
    /// in Hex's words, all of them set at once, then the preview with one switch turned off leaving that one alone. Then
    /// VS Code's name for a setting written in Hex's file said in Problems with Hex's name. The user's file is the test's.
    /// </summary>
    private void TestVsCodeImport(HexPointerScript test, Func<string> userFile)
    {
        string json = Path.Combine(testFolder, "selftest_vscode_settings.json");
        const string TEXT = "{\n    // the way VS Code writes one\n    \"editor.tabSize\": 3,\n    /* rulers at two columns */\n    \"editor.rulers\": [80, 100],\n"
            + "    \"files.associations\": { \"*.orig\": \"csharp\" },\n    \"editor.fontSize\": 21,\n    \"editor.wordWrap\": \"on\",\n    \"foo.bar\": 1,\n}\n";
        VsCodeImported? imported = null;

        void Start(int tabSize)
        {
            new TextSettingsSource(this).Set("tab_size", new NumberValue(tabSize));
            new EditorSource(this).Set("wrap_code", Values.Bool(false));
            new EditorSource(this).Set("code_zoom", new NumberValue(1.0f));
        }

        test.Check("vscode: JSONC parse", () =>
        {
            File.WriteAllText(json, TEXT);
            var read = VsCodeSettings.Read(File.ReadAllText(json));
            return read.Count == 6 && read["editor.tabSize"].GetInt32() == 3 && read["foo.bar"].GetInt32() == 1 && read["editor.rulers"].GetArrayLength() == 2;
        });

        test.Check("vscode: mapping", () =>
        {
            string[] fonts = ["hack", "cascadia_mono"];
            var plan = VsCodeSettings.Plan(VsCodeSettings.Read(TEXT), fonts).ToDictionary(setting => setting.Id);
            var family = VsCodeSettings.Plan(VsCodeSettings.Read("{ \"editor.fontFamily\": \"'Fira Code', 'Cascadia Mono', monospace\", \"editor.cursorStyle\": \"block-outline\" }"), fonts);
            return plan["editor.tabSize"] is { Key: "tab_size", Value: NumberValue { Value: 3.0f } }
                && plan["editor.rulers"] is { Key: "rulers", Value: StringValue { Value: "80, 100" } }
                && plan["files.associations"] is { Key: "files_associations", Value: ObjectValue { Properties: { } pairs } } && pairs["*.orig"] is StringValue { Value: "csharp" }
                && plan["editor.fontSize"] is { Key: "code_zoom", Value: NumberValue { Value: 1.5f } }
                && plan["editor.wordWrap"] is { Key: "wrap_code", Value: BooleanValue { Value: true } }
                && plan["foo.bar"] is { Key: null, Mapped: false }
                && family[0] is { Key: "code_font", Value: StringValue { Value: "cascadia_mono" } } && family[1].Value is StringValue { Value: "block" };
        });

        test.Run(() =>
        {
            Start(4);
            imported = Import(json, all: true);
        });
        test.Wait();
        test.Check("vscode: apply", () =>
            settings.TabSize == 3 && HexSettings.RulersText(settings.Rulers) == "80, 100" && settings.FileAssociations.GetValueOrDefault("*.orig") == "csharp"
            && MathF.Abs(settings.CodeZoom - 1.5f) < 0.01f && settings.WrapCode && imported is { Set: 5 });
        test.Check("vscode: unmapped report", () => imported is { Left: ["foo.bar"] }
            && toasts.Cards.Any(card => !card.Closed && card.Text.Contains("5 VS Code settings imported, 1 left out", StringComparison.Ordinal)));

        // The preview, a switch a setting and foo.bar dim, the tab size's switch off leaving the tab size as it was
        List<ToggleButton> toggles = [];
        test.Run(() =>
        {
            Start(4);
            Import(json, all: false);
            if (textDialog is { IsOpen: true } dialog)
                toggles = [.. Everything(dialog.Body).OfType<ToggleButton>()];
        });
        test.Wait();
        test.Check("vscode: preview shows mapped and unmapped rows", () =>
            textDialog is { IsOpen: true } dialog && toggles.Count == 5 && toggles.All(toggle => toggle.State)
            && toggles[0].Label == "editor.tabSize 3 -> " + Array.Find(EditorSettingsCatalog.Text, row => row.Key == "tab_size")!.Title + " 3" && dialog.Buttons[0].Label == "Import 5 settings"
            && Everything(dialog.Body).OfType<Label>().Any(label => label.Text.Contains("foo.bar 1, not in Hex yet", StringComparison.Ordinal)));
        test.Run(() =>
        {
            toggles[0].State = false;
            toggles[0].OnPressed?.Invoke();
        });
        test.Wait();
        test.Check("vscode: the button counts what's ticked", () => textDialog?.Buttons[0].Label == "Import 4 settings");
        test.Run(() => textDialog?.Buttons[0].OnPressed?.Invoke());
        test.Wait();
        test.Check("vscode: unticking a row skips it", () =>
            textDialog is not { IsOpen: true } && settings.TabSize == 4 && settings.WrapCode && MathF.Abs(settings.CodeZoom - 1.5f) < 0.01f);

        test.Check("vscode: palette lists both import commands", () =>
            AllCommands() is var all && all.Any(command => command.Text == "Preferences: Import VS Code Settings...")
            && all.Any(command => command.Text == "Preferences: Import VS Code Workspace Settings..."));

        // VS Code's name in Hex's own file, said with Hex's at its line. Writing the file puts every setting back too
        IEnumerable<HexProblem> Mine() => problemList.Where(problem => PlatformPaths.Same(problem.Path, userFile()));
        test.Run(() =>
        {
            File.Delete(json);
            File.WriteAllText(userFile(), "let settings = {\n    editor: {\n        \"editor.tabSize\": 2\n    }\n};\n");
        });
        test.WaitFor(() => Mine().Any(), 3.0f);
        test.Check("vscode: diagnostic", () =>
            Mine().ToList() is [{ Line: 2, Column: 8, Warning: true } only] && only.Text == "editor.tabSize is VS Code's name, Hex calls it tab_size");
        test.Run(() => File.WriteAllText(userFile(), "let settings = {\n    editor: {\n        tab_size: 4\n    }\n};\n"));
        test.WaitFor(() => !Mine().Any() && settings.TabSize == 4, 3.0f);
    }

    // Everything inside a component, all the way down
    private static IEnumerable<UIComponent> Everything(UIComponent of)
    {
        foreach (UIComponent child in of.Children)
        {
            yield return child;
            foreach (UIComponent deeper in Everything(child))
                yield return deeper;
        }
    }

    // The card that says the settings file doesn't read
    private UINotification? BrokenCard(string file) =>
        toasts.Cards.LastOrDefault(card => !card.Closed && card.Text.Contains(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase) && card.Text.Contains("left as they were", StringComparison.Ordinal));
}
