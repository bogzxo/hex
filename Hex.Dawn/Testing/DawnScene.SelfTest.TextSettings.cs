using Hex.Shell;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of Editor, Text in the settings. Its rows at the top of the window with help and a reset, the tab size
// and spaces or tabs going straight onto the open files and what Tab puts in, a file indented with tabs kept that way,
// a project's .editorconfig winning over the settings, the line numbers going, the values kept through the file, and
// no dots on sections nobody changed. Then the ones a VS Code developer reaches for every day, the rulers, whitespace,
// the caret's look and sticky scroll on every open editor, all of them a folder's to set, found by their VS Code ids,
// and format on save writing a file with no server straight away.
internal sealed partial class DawnScene
{
    private void TestTextSettings(HexPointerScript test)
    {
        string folder = string.Empty, plain = string.Empty, tabbed = string.Empty, yaml = string.Empty, spaced = string.Empty;
        HexDocument? before = null;
        HexDocument? Opened(string file) => documents.FirstOrDefault(open => open.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(file));
        string[] keys = [.. EditorSettingsCatalog.Text.Select(setting => setting.Key)];
        string[] textKeys = [.. EditorSettingsCatalog.Text.Where(setting => setting.Group == EditorSettingsCatalog.TEXT).Select(setting => setting.Key)];

        test.Run(() =>
        {
            before = document;
            folder = Path.Combine(testFolder, "text_settings");
            Directory.CreateDirectory(folder);
            plain = Path.Combine(folder, "plain.txt");
            tabbed = Path.Combine(folder, "tabbed.cs");
            File.WriteAllText(plain, "alpha\nbeta\n");
            File.WriteAllText(tabbed, "class A\n{\n\tint b;\n\tvoid C()\n\t{\n\t\tb = 1;\n\t}\n}\n");

            // Spaces, a tab and spaces left trailing, and a line long enough to cross the rulers, for the picture of them
            spaced = Path.Combine(folder, "spaced.cs");
            File.WriteAllText(spaced, "class Spaced\n{\n    int a = 1;   \n\tint b = 2;\n    // a comment long enough to run on past the rulers drawn down the code at forty and eighty columns\n}\n");

            // A project of its own whose .editorconfig says yaml goes in twos, the file in fours
            string project = Path.Combine(folder, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, HexEditorConfig.FILE), "root = true\n\n[*]\nindent_style = space\nindent_size = 4\n\n[*.{yml,yaml}]\nindent_size = 2 ; comment\n");
            yaml = Path.Combine(project, "list.yaml");
            File.WriteAllText(yaml, "list:\n    one: 1\n    two:\n        deeper: 2\n");
            Open(plain);
            Open(tabbed);
            Open(yaml);
            Open(spaced);
        });
        test.Wait();

        /* The rows */

        test.Run(() => OpenSettings());
        test.Wait();
        string[] titles = ["Tab size", "Insert spaces", "Detect indentation", "Line numbers", "Indent guides", "Use .editorconfig"];
        test.Check("text settings: the window opens on Editor · Text, its rows first", () =>
            SettingsPage is { } page && page.SectionsShown.Count > 0 && page.SectionsShown[0] == EditorSettingsCatalog.TEXT
            && page.Settings.Where(setting => setting.Group == EditorSettingsCatalog.TEXT).Select(setting => setting.Key).SequenceEqual(textKeys));

        // A row's name is written in bold, so it's looked for inside what the row says
        test.Check("text settings: they're called Tab size, Insert spaces, Detect indentation, Line numbers, Indent guides and Use .editorconfig", () =>
            SettingsPage is { } page && keys.Zip(titles).All(pair => page.TitleShown(pair.First) is { } shown && shown.Contains(pair.Second)));
        test.Check("text settings: each has its help, the tab rows saying .editorconfig wins, and none is changed yet", () =>
            SettingsPage is { } page && keys.All(key => page.HelpShown(key) is { Length: > 20 } && !page.IsModified!(key))
            && page.HelpShown("tab_size")!.Contains(".editorconfig") && page.HelpShown("insert_spaces")!.Contains(".editorconfig"));
        test.Check("text settings: nothing changed shows no dot on Editor · Files or Terminal · System", () =>
            SettingsPage is { } page && !page.SectionsChanged.Contains(EditorSettingsCatalog.FILES) && !page.SectionsChanged.Contains("Terminal · System")
            && !page.SectionsChanged.Contains(EditorSettingsCatalog.TEXT));
        test.Run(() => SettingsPage!.Find("tab"));
        test.Wait();
        test.Check("text settings: searching tab finds Tab size", () => SettingsPage!.IsRowShown("tab_size"));
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Wait();

        /* Tab size and spaces or tabs, straight onto the open files */

        test.Run(() => SettingsPage!.Enter("tab_size", new NumberValue(2.0f)));
        test.Check("text settings: a tab size of 2 is every open plain file's, and Tab puts in 2 spaces", () =>
        {
            if (Opened(plain) is not { } file || settings.TabSize != 2 || !SettingsPage!.IsModified!("tab_size"))
                return false;

            CodeEditor editor = file.Editor;
            editor.Document.CollapseCarets();
            editor.Document.MoveTo(new UITextPosition(0, 0));
            editor.IndentOrTab();
            bool typed = editor.Document.LineAt(0) == "  alpha";
            editor.Document.Undo();
            return typed && editor.TabSize == 2 && editor.Document.Indent == "  " && editor.Document.LineAt(0) == "alpha";
        });

        test.Run(() => SettingsPage!.Enter("insert_spaces", Values.Bool(false)));
        test.Check("text settings: insert spaces off and Tab puts in a tab", () =>
        {
            if (Opened(plain) is not { } file || settings.InsertSpaces)
                return false;

            CodeEditor editor = file.Editor;
            editor.Document.CollapseCarets();
            editor.Document.MoveTo(new UITextPosition(1, 0));
            editor.IndentOrTab();
            bool typed = editor.Document.LineAt(1) == "\tbeta";
            editor.Document.Undo();
            return typed && editor.Document.Indent == "\t" && editor.Document.LineAt(1) == "beta";
        });
        test.Run(() =>
        {
            SettingsPage!.Reset("insert_spaces");
            SettingsPage!.Reset("tab_size");
        });
        test.Check("text settings: and their resets put them back", () =>
            settings.TabSize == 4 && settings.InsertSpaces && !SettingsPage!.IsModified!("tab_size") && !SettingsPage!.IsModified!("insert_spaces")
            && Opened(plain) is { Editor: { TabSize: 4, Document.Indent: "    " } });

        /* What a file is already indented with, and the project's .editorconfig */

        test.Check("text settings: a file indented with tabs keeps tabs with detect indentation on", () =>
            settings.DetectIndentation && Opened(tabbed) is { Editor.Document.Indent: "\t" });
        test.Check("text settings: the .editorconfig's [*.{yml,yaml}] indent_size = 2 wins over the settings and the file's fours", () =>
            settings.UseEditorConfig && Opened(yaml) is { Editor: { TabSize: 2, Document.Indent: "  " } });

        // The status bar says what the file on screen is indented with, VS Code's Spaces 2 and Tab Size 4
        test.Run(() => Show(Opened(yaml)!));
        test.Wait();
        test.Check("text settings: the status bar says Spaces: 2 for the yaml", () => indentItem.Text == "Spaces: 2");
        test.Run(() => Show(Opened(tabbed)!));
        test.Wait();
        test.Check("text settings: and Tab Size: 4 for the file indented with tabs", () => indentItem.Text == "Tab Size: 4");
        test.Run(() => SettingsPage!.Enter("detect_indentation", Values.Bool(false)));
        test.Check("text settings: detect indentation off and the tabbed file goes by the settings", () =>
            !settings.DetectIndentation && Opened(tabbed) is { Editor.Document.Indent: "    " });
        test.Run(() => SettingsPage!.Enter("editorconfig", Values.Bool(false)));
        test.Check("text settings: .editorconfig off and the yaml goes by the settings too", () =>
            !settings.UseEditorConfig && Opened(yaml) is { Editor: { TabSize: 4, Document.Indent: "    " } });
        test.Run(() =>
        {
            SettingsPage!.Reset("detect_indentation");
            SettingsPage!.Reset("editorconfig");
        });
        test.Check("text settings: the reader of .editorconfig globs matches names, braces and folders", () =>
            HexEditorConfig.Matches("*", "a/b.txt") && HexEditorConfig.Matches("*.{yml,yaml}", "deep/list.yaml") && !HexEditorConfig.Matches("*.{yml,yaml}", "list.json")
            && HexEditorConfig.Matches("Makefile", "src/Makefile") && !HexEditorConfig.Matches("Makefile", "src/Makefile.am")
            && HexEditorConfig.Matches("src/**.cs", "src/a/b.cs") && !HexEditorConfig.Matches("src/*.cs", "src/a/b.cs")
            && HexEditorConfig.Values(new Dictionary<string, string> { ["indent_style"] = "tab", ["indent_size"] = "tab", ["tab_width"] = "8" }) is { UseTabs: true, IndentSize: 8, TabWidth: 8 });

        /* Line numbers and guides */

        test.Run(() =>
        {
            SettingsPage!.Enter("line_numbers", Values.Bool(false));
            SettingsPage!.Enter("indent_guides", Values.Bool(false));
        });
        test.Check("text settings: line numbers off takes them off every open editor, and the guides with indent guides off", () =>
            !settings.LineNumbers && documents.All(open => !open.Editor.LineNumbers && !open.Editor.IndentGuides));
        test.Run(() => Shot("text-settings-no-numbers"));
        test.Run(() =>
        {
            SettingsPage!.Reset("line_numbers");
            SettingsPage!.Reset("indent_guides");
        });
        test.Check("text settings: and back on with their resets, nothing in the section left changed", () =>
            settings.LineNumbers && settings.IndentGuides && documents.All(open => open.Editor.LineNumbers && open.Editor.IndentGuides)
            && keys.All(key => !SettingsPage!.IsModified!(key)));

        /* The rulers, the whitespace, the caret and sticky scroll, VS Code's everyday ones */

        test.Run(() => SettingsPage!.Enter("rulers", new StringValue("80, 120")));
        test.Check("text: rulers on every editor", () =>
            settings.Rulers.SequenceEqual([80, 120]) && documents.All(open => open.Editor.Rulers.SequenceEqual([80, 120]))
            && SettingsPage!.IsModified!("rulers"));
        test.Run(() => SettingsPage!.Enter("rulers", new StringValue("abc, 90")));
        test.Check("text: rulers drop what isn't a column, abc, 90 is just 90", () =>
            settings.Rulers.SequenceEqual([90]) && documents.All(open => open.Editor.Rulers.SequenceEqual([90]))
            && HexSettings.ParseRulers("0, 501, 12x, 7, 7") is [7]);

        test.Run(() =>
        {
            SettingsPage!.Enter("render_whitespace", new StringValue("all"));
            SettingsPage!.Enter("cursor_style", new StringValue("block"));
        });
        test.Check("text: whitespace and cursor style", () =>
            settings is { RenderWhitespace: "all", CursorStyle: "block" }
            && documents.All(open => open.Editor is { RenderWhitespace: UIWhitespace.All, CaretStyle: UICaretStyle.Block }));

        // A picture of the rulers and the dots and arrows, the settings put away for it
        test.Run(() =>
        {
            SettingsPage!.Enter("rulers", new StringValue("40, 80"));
            CloseSettings();
            Show(Opened(spaced)!);
        });
        test.Wait();
        test.Run(() => Shot("text-settings-rulers-whitespace"));
        test.Run(() => OpenSettings());
        test.Wait();
        test.Run(() =>
        {
            SettingsPage!.Reset("rulers");
            SettingsPage!.Reset("render_whitespace");
            SettingsPage!.Reset("cursor_style");
        });
        test.Check("text: and their resets put selection, line and no rulers back", () =>
            settings is { RenderWhitespace: "selection", CursorStyle: "line", Rulers.Length: 0 }
            && documents.All(open => open.Editor is { RenderWhitespace: UIWhitespace.Selection, CaretStyle: UICaretStyle.Line, Rulers.Length: 0 })
            && !SettingsPage!.IsModified!("render_whitespace") && !SettingsPage!.IsModified!("cursor_style") && !SettingsPage!.IsModified!("rulers"));

        test.Run(() => SettingsPage!.Enter("sticky_scroll", Values.Bool(false)));
        test.Check("text: sticky scroll toggle", () =>
            !settings.StickyScroll && documents.All(open => !open.Editor.StickyScroll));
        test.Run(() => SettingsPage!.Reset("sticky_scroll"));
        test.Check("text: and sticky scroll back on with its reset", () =>
            settings.StickyScroll && documents.All(open => open.Editor.StickyScroll));

        test.Check("workspace: new keys are folder keys", () =>
            new[] { "rulers", "render_whitespace", "cursor_style", "sticky_scroll", "format_on_save" }.All(HexSettings.IsWorkspaceKey));
        test.Check("text: format on save is a row of Editor · Files", () =>
            SettingsPage!.Settings.Any(setting => setting.Key == "format_on_save" && setting.Group == EditorSettingsCatalog.FILES));

        // VS Code's own names for them find them, the way somebody coming from it would type them
        test.Run(() => SettingsPage!.Find("editor.tabSize"));
        test.Wait();
        test.Check("search: VS Code id", () =>
            SettingsPage!.IsRowShown("tab_size") && !SettingsPage.IsRowShown("insert_spaces") && !SettingsPage.IsRowShown("line_numbers"));
        test.Run(() => SettingsPage!.Find("editor.renderWhitespace"));
        test.Wait();
        test.Check("search: VS Code ids of the new rows too", () =>
            SettingsPage!.IsRowShown("render_whitespace") && !SettingsPage.IsRowShown("tab_size"));
        test.Run(() => SettingsPage!.Find(string.Empty));
        test.Wait();

        TestExcludeSettings(test);

        /* Kept in the file */

        // Only what isn't what it comes as is written, the ones changed here and nothing else of the editor's
        test.Check("text settings: the values go through a settings file and come back the same, only the changed ones written", () =>
        {
            string file = Path.Combine(folder, "text_settings.hor");
            File.Delete(file);
            var written = new HexSettings(file)
            {
                TabSize = 3,
                InsertSpaces = false,
                DetectIndentation = false,
                LineNumbers = false,
                IndentGuides = false,
                UseEditorConfig = false,
                Rulers = [80, 120],
                RenderWhitespace = "boundary",
                CursorStyle = "underline",
                StickyScroll = false,
                FormatOnSave = true,
                FilesExclude = ["**/*.log"],
                SearchExclude = ["**/fixtures", "vendor/"],
                SearchUseIgnoreFiles = false
            };
            if (written.Save() is not null)
                return false;

            var read = new HexSettings(file);
            return read is { TabSize: 3, InsertSpaces: false, DetectIndentation: false, LineNumbers: false, IndentGuides: false, UseEditorConfig: false }
                && read is { RenderWhitespace: "boundary", CursorStyle: "underline", StickyScroll: false, FormatOnSave: true } && read.Rulers.SequenceEqual([80, 120])
                && read is { SearchUseIgnoreFiles: false } && read.FilesExclude.SequenceEqual(["**/*.log"]) && read.SearchExclude.SequenceEqual(["**/fixtures", "vendor/"])
                && SettingsSections.Read(file) is { } all && SettingsSections.Section(all, SettingsSections.EDITOR).Keys.Order(StringComparer.Ordinal)
                    .SequenceEqual(keys.Order(StringComparer.Ordinal));
        });

        // Formatting on save, a plain text file has no server that formats, so Ctrl+S writes it there and then
        string onDisk = string.Empty;
        test.Run(() =>
        {
            SettingsPage!.Enter("format_on_save", Values.Bool(true));
            CloseSettings();
            Show(Opened(plain)!);
            code.Document.MoveTo(new UITextPosition(0, 0));
            code.Document.Insert("gamma ");
            compositor.Focus = code;
            commands.Keys.Update(key => key == Silk.NET.Input.Key.S, control: true, shift: false, alt: false, typing: true);
            onDisk = File.ReadAllText(plain);
        });
        test.Check("text: format on save without a server saves", () =>
            settings.FormatOnSave && onDisk.StartsWith("gamma alpha", StringComparison.Ordinal) && Opened(plain) is { Modified: false }
            && messageItem.Color is null);
        test.Run(() => OpenSettings());
        test.Wait();
        test.Run(() => SettingsPage!.Reset("format_on_save"));
        test.Check("text: and format on save off again with its reset", () => !settings.FormatOnSave);

        test.Run(() =>
        {
            CloseSettings();
            foreach (string file in new[] { plain, tabbed, yaml, spaced })
            {
                if (Opened(file) is { } open)
                {
                    Show(open);
                    CloseNow();
                }
            }

            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
    }
}
