using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the settings round on the window itself. File associations as a list of pairs set with the pointer
// and the keyboard, search_exclude as a list of globs the same way, every free text row saying what goes in it, the page using a big screen, and the sections down the
// left following a search. The pairs in the file and the file's problems agreeing with what reading it does are at the
// end, see TestSettingsPairsFile and TestSettingsTruthfulProblems
internal sealed partial class DawnScene
{
    private const string ASSOCIATIONS = "files_associations";

    private void TestSettingsPairs(HexPointerScript test)
    {
        SettingsPage page = null!;
        IReadOnlyList<(string Key, string Value)> Shown() => page.PairsShown(ASSOCIATIONS)?.Pairs ?? [];
        IReadOnlyList<(string, string)> Kept() => UIFormPairs.Of(page.ValueOf!(ASSOCIATIONS));
        void Type(string key, string value) => test.Run(() =>
        {
            if (page.PairsShown(ASSOCIATIONS) is { KeyBox: { } keyBox, ValueBox: { } valueBox })
                (keyBox.Text, valueBox.Text) = (key, value);
        });

        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
            page.Reveal(ASSOCIATIONS);
        });
        test.Settle(0.4f);
        test.Check("settings pairs: Editor, Files has Files associations as a list of pairs with Add Item, empty as it comes", () =>
            page.Settings.FirstOrDefault(setting => setting.Key == ASSOCIATIONS) is { Kind: UIFormKind.Pairs, Group: EditorSettingsCatalog.FILES } row
            && row.KeyPlaceholder == "*.orig" && row.ValuePlaceholder == "csharp" && row.Help.Contains("Ctrl+K M", StringComparison.Ordinal)
            && page.PairBounds(ASSOCIATIONS) is { AddItem.IsEmpty: false } && Shown().Count == 0 && !page.IsModified!(ASSOCIATIONS));

        // Add Item with the pointer, two boxes with what goes in them, Enter puts the pair in
        test.Check("settings pairs: it's called File associations", () => page.TitleShown(ASSOCIATIONS) is { } title && title.Contains("File associations", StringComparison.Ordinal));
        test.Run(() => Shot("settings-pairs-empty"));
        test.Click(() => page.PairBounds(ASSOCIATIONS) is { } parts ? chrome.ToWorld(parts.AddItem.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings pairs: Add Item opens a line of two boxes with the keyboard in the first, *.orig and csharp in them dim", () =>
            page.PairsShown(ASSOCIATIONS) is { Editing: 0, KeyBox: { Placeholder: "*.orig" } keyBox, ValueBox.Placeholder: "csharp" } && compositor.Focus == keyBox);
        test.Check("settings pairs: adding one puts out the flash of going to the row, no flood of the accent over the line being typed", () =>
            page.FlashingKey is null);
        test.Run(() => Shot("settings-pairs-typing"));
        Type("*.orig", "csharp");
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings pairs: Enter puts *.orig = csharp in, the row changed with its reset, the keyboard on the line's pencil", () =>
            Kept().SequenceEqual([("*.orig", "csharp")]) && Shown().Count == 1 && page.IsModified!(ASSOCIATIONS)
            && page.RowBounds(ASSOCIATIONS) is { Reset.IsEmpty: false } && page.KeyWithKeyboard == ASSOCIATIONS && page.PairsShown(ASSOCIATIONS)!.Value.Editing < 0);
        test.Run(() => Shot("settings-pairs-one"));

        // The keyboard, Enter on the pencil opens the line, Escape leaves it as it was
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings pairs: Enter on the pencil opens the line with what it has", () =>
            page.PairsShown(ASSOCIATIONS) is { Editing: 0, KeyBox.Text: "*.orig", ValueBox.Text: "csharp" });
        Type("*.orig", "rust");
        test.Run(() => page.Press(UIKeyboard.ESCAPE));
        test.Wait();
        test.Check("settings pairs: Escape leaves it as it was, the window still up", () =>
            Kept().SequenceEqual([("*.orig", "csharp")]) && page.PairsShown(ASSOCIATIONS)!.Value.Editing < 0 && SettingsPage is not null && page.KeyWithKeyboard == ASSOCIATIONS);

        // The pencil with the pointer, a new value, Enter
        test.Click(() => page.PairBounds(ASSOCIATIONS) is { Edits.Length: > 0 } parts ? chrome.ToWorld(parts.Edits[0].Center) : test.Pointer.Position);
        test.Wait();
        Type("*.orig", "plaintext");
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings pairs: the pencil with the pointer edits the value", () => Kept().SequenceEqual([("*.orig", "plaintext")]));

        // Down along the line's buttons to Add Item, a second pair by the keyboard alone
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Run(() => page.Press(UIKeyboard.DOWN));
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings pairs: Down goes along the buttons to Add Item and Enter on it opens a new line", () =>
            page.PairsShown(ASSOCIATIONS) is { Editing: 1, KeyBox: { } keyBox } && compositor.Focus == keyBox);
        Type("*.log", "log");
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings pairs: and Enter puts the second one in after the first", () => Kept().SequenceEqual([("*.orig", "plaintext"), ("*.log", "log")]));

        // A search finds the row by a key or a value, the pair it was found by after its key
        test.Run(() => page.Find("plaintext"));
        test.Wait();
        test.Check("settings pairs: searching a value finds the row, the pair said after its key", () =>
            page.IsRowShown(ASSOCIATIONS) && page.FoundByShown(ASSOCIATIONS) is { } by && by.Contains("*.orig", StringComparison.Ordinal));
        test.Run(() => page.Find(string.Empty));
        test.Wait();

        // The bin with the pointer
        test.Run(() => page.Reveal(ASSOCIATIONS));
        test.Settle(0.3f);
        test.Click(() => page.PairBounds(ASSOCIATIONS) is { Removes.Length: > 1 } parts ? chrome.ToWorld(parts.Removes[1].Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings pairs: the bin takes its pair out", () => Kept().SequenceEqual([("*.orig", "plaintext")]) && Shown().Count == 1);

        // Its reset empties it
        test.Run(() => page.Reset(ASSOCIATIONS));
        test.Wait();
        test.Check("settings pairs: its reset empties it and it's no longer changed", () => Kept().Count == 0 && Shown().Count == 0 && !page.IsModified!(ASSOCIATIONS));

        TestSettingsList(test);

        /* Every line of text says what goes in it */

        test.Check("settings placeholders: every free text row of the editor's says what goes in it, the rulers 80, 120", () =>
            page.Settings.Where(setting => setting.Kind == UIFormKind.Text && !setting.ButtonsOnly && setting.Group.StartsWith("Editor", StringComparison.Ordinal)
                && EditorSettingsCatalog.Settings.Concat(EditorSettingsCatalog.Text).Any(row => row.Key == setting.Key)).ToList() is { Count: >= 2 } rows
            && rows.All(setting => setting.Placeholder.Length > 0)
            && page.Settings.First(setting => setting.Key == "backdrop_image").Placeholder.Length > 0
            && Descendants(SettingsWindow!).OfType<TextBox>().Any(box => box.Placeholder == "80, 120"));

        TestSettingsBigDisplay(test);
        TestSettingsNavFollowsSearch(test);
    }

    // Search exclude as a list, VS Code's search.exclude, a line a glob with a pencil and a bin and Add Pattern under them.
    // Added with the pointer and put in with Enter, edited and left with the keyboard, and taken out with Delete
    private void TestSettingsList(HexPointerScript test)
    {
        const string SEARCH_EXCLUDE = "search_exclude";
        SettingsPage page = null!;
        IReadOnlyList<string> Shown() => page.ListShown(SEARCH_EXCLUDE)?.Items ?? [];
        void Type(string item) => test.Run(() =>
        {
            if (page.ListShown(SEARCH_EXCLUDE) is { Box: { } box })
                box.Text = item;
        });

        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
            page.Reveal(SEARCH_EXCLUDE);
        });
        test.Settle(0.4f);
        test.Check("settings list: Editor · Search has files_exclude and search_exclude as lists with Add Pattern, and Use .gitignore", () =>
            page.Settings.FirstOrDefault(setting => setting.Key == SEARCH_EXCLUDE) is { Kind: UIFormKind.List, Group: EditorSettingsCatalog.SEARCH, ValuePlaceholder: "Add Pattern", KeyPlaceholder: "**/fixtures" }
            && page.Settings.Any(setting => setting is { Key: "files_exclude", Kind: UIFormKind.List, Group: EditorSettingsCatalog.SEARCH })
            && page.Settings.Any(setting => setting is { Key: "search_use_ignore_files", Kind: UIFormKind.Flag })
            && EditorSettingsCatalog.Sections.SkipWhile(section => section != EditorSettingsCatalog.FILES).Skip(1).FirstOrDefault() == EditorSettingsCatalog.SEARCH);
        test.Check("settings list: VS Code's search.exclude as it comes, a pencil and a bin a line, Add Pattern under them", () =>
            Shown().SequenceEqual(HexSettings.UsualSearchExclude) && page.ListBounds(SEARCH_EXCLUDE) is { Add.IsEmpty: false } parts
            && parts.Edits.Length == Shown().Count && parts.Removes.Length == Shown().Count && !page.IsModified!(SEARCH_EXCLUDE));

        test.Click(() => page.ListBounds(SEARCH_EXCLUDE) is { } parts ? chrome.ToWorld(parts.Add.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings list: Add Pattern opens a box at the end with the keyboard in it, **/fixtures in it dim, and no flash", () =>
            page.ListShown(SEARCH_EXCLUDE) is { Editing: var editing, Box: { Placeholder: "**/fixtures" } box } && editing == HexSettings.UsualSearchExclude.Length
            && compositor.Focus == box && page.FlashingKey is null);
        test.Run(() => Shot("settings-list-typing"));
        Type("**/fixtures");
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings list: Enter keeps **/fixtures, at the end, the row changed and the keyboard on its pencil", () =>
            settings.SearchExclude.LastOrDefault() == "**/fixtures" && Shown().LastOrDefault() == "**/fixtures" && page.IsModified!(SEARCH_EXCLUDE)
            && page.ListShown(SEARCH_EXCLUDE)!.Value.Editing < 0 && page.KeyWithKeyboard == SEARCH_EXCLUDE);
        test.Run(() => Shot("settings-list-one"));

        // The keyboard, Enter on the pencil opens the line with what it has, Escape leaves it, Enter again keeps a new one
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings list: Enter on the pencil opens the line with what it has", () =>
            page.ListShown(SEARCH_EXCLUDE) is { Box.Text: "**/fixtures" } shown && shown.Editing == Shown().Count - 1);
        Type("**/nope");
        test.Run(() => page.Press(UIKeyboard.ESCAPE));
        test.Wait();
        test.Check("settings list: Escape leaves it as it was, the window still up", () =>
            settings.SearchExclude.LastOrDefault() == "**/fixtures" && page.ListShown(SEARCH_EXCLUDE)!.Value.Editing < 0 && SettingsPage is not null
            && page.KeyWithKeyboard == SEARCH_EXCLUDE);
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        Type("**/test_data/");
        test.Run(() => page.Press(UIKeyboard.ENTER));
        test.Wait();
        test.Check("settings list: and Enter keeps what it's edited to in its place", () =>
            settings.SearchExclude.LastOrDefault() == "**/test_data/" && !settings.SearchExclude.Contains("**/fixtures") && Shown().Count == HexSettings.UsualSearchExclude.Length + 1);

        // Found by a glob in it, the glob said after its key
        test.Run(() => page.Find("pycache"));
        test.Wait();
        test.Check("settings list: searching a glob in it finds the row, the glob said after its key", () =>
            page.IsRowShown(SEARCH_EXCLUDE) && page.FoundByShown(SEARCH_EXCLUDE) is { } by && by.StartsWith("**/__", StringComparison.Ordinal) && by.Contains("pycache", StringComparison.Ordinal));
        test.Run(() =>
        {
            page.Find(string.Empty);
            page.Reveal(SEARCH_EXCLUDE);
        });
        test.Settle(0.3f);

        // Delete on its pencil takes the line out
        test.Run(() =>
        {
            if (page.ListBounds(SEARCH_EXCLUDE) is { Edits.Length: > 0 } parts)
                Descendants(SettingsWindow!).OfType<Button>().FirstOrDefault(button => button.Bounds == parts.Edits[^1])?.Focus();
            page.Press(UIKeyboard.DELETE);
        });
        test.Wait();
        test.Check("settings list: Delete on a line's pencil takes it out, back to what it comes as", () =>
            settings.SearchExclude.SequenceEqual(HexSettings.UsualSearchExclude) && Shown().SequenceEqual(HexSettings.UsualSearchExclude)
            && !page.IsModified!(SEARCH_EXCLUDE) && page.KeyWithKeyboard == SEARCH_EXCLUDE);

        // The bin with the pointer, and the reset puts it back
        test.Click(() => page.ListBounds(SEARCH_EXCLUDE) is { Removes.Length: > 0 } parts ? chrome.ToWorld(parts.Removes[0].Center) : test.Pointer.Position);
        test.Wait();
        test.Check("settings list: the bin takes its glob out", () => !settings.SearchExclude.Contains("**/node_modules") && page.IsModified!(SEARCH_EXCLUDE));
        test.Run(() => page.Reset(SEARCH_EXCLUDE));
        test.Wait();
        test.Check("settings list: its reset puts VS Code's back", () =>
            settings.SearchExclude.SequenceEqual(HexSettings.UsualSearchExclude) && Shown().SequenceEqual(HexSettings.UsualSearchExclude) && !page.IsModified!(SEARCH_EXCLUDE));
    }

    // The page on a big screen, the window most of it, the rows past their old 1120 and the text zoomed to suit
    private void TestSettingsBigDisplay(HexPointerScript test)
    {
        test.Check("settings big display: at 1600 by 900 the window opens the size it always did", () =>
            SettingsWindow is { } window && Horizon.UI.Components.SettingsWindow.SizeFor(new Vector2(1600.0f, 900.0f), window.MinSize) == new Vector2(1152.0f, 702.0f)
            && ScaleFor(new Vector2(1600.0f, 900.0f)) == 1.0f);
        test.Check("settings big display: on a big screen the window is about 80% of it, the rows wider than 1120, the text zoomed", () =>
            SettingsWindow is { } window && SettingsPage is { } page && chrome.Root.Bounds.Size is var room
            && (room.X < 3000.0f || (Near(window.Size.X, room.X * 0.8f, room.X * 0.03f) && Near(window.Size.Y, room.Y * 0.8f, room.Y * 0.03f)
                && page.RowWidthNow > 1120.0f && page.Zoom == SettingsZoom && page.ZoomNow > 1.0f)));
    }

    // While a search is typed the sections down the left are the ones it found something in, the best first and with
    // how many, and emptied it's the whole tree again scrolled where it was
    private void TestSettingsNavFollowsSearch(HexPointerScript test)
    {
        SettingsPage page = null!;
        IReadOnlyList<string> before = [];
        float scrolled = 0.0f;
        test.Run(() =>
        {
            page = SettingsPage!;
            page.Find(string.Empty);
            page.GoTo(EditorSettingsCatalog.GIT);
        });
        test.Settle(0.4f);
        test.Run(() => (before, scrolled) = (page.SectionsShown, page.RowsOffset));
        test.Run(() => page.Find("font"));
        test.Wait();
        test.Run(() => Shot("settings-nav-search"));
        test.Check("settings nav search: font leaves only the sections it found something in down the left, each with how many", () =>
            page.SectionCounts is { Count: > 0 } counts && counts.All(each => each.Count > 0)
            && counts.Select(each => each.Group).ToHashSet().SetEquals(page.KeysShown.Select(key => page.Settings.First(setting => setting.Key == key).Group)));
        test.Check("settings nav search: the section of the best row first", () =>
            page.SectionsShown.FirstOrDefault() is { } first && page.KeysShown.FirstOrDefault() is { } best
            && first == page.Settings.First(setting => setting.Key == best).Group);
        test.Run(() => page.Find(string.Empty));
        test.WaitFor(() => !page.ScrollingBack, 2.0f);
        test.Settle(0.2f);
        test.Check("settings nav search: emptied, the whole tree is back and the rows are scrolled where they were", () =>
            page.SectionsShown.SequenceEqual(before) && scrolled > 0.0f && Near(page.RowsOffset, scrolled, 2.0f));
    }

    // Files associations in the settings file, written as an object, read back after the file is edited and saved, and
    // gone from it once it's reset. The file is the self test's own, see TestSettingsFile
    private void TestSettingsPairsFile(HexPointerScript test, Func<string> file)
    {
        Dictionary<string, IRuntimeValue> Editor() => SettingsSections.Section(SettingsSections.Read(file()) ?? [], SettingsSections.EDITOR);

        test.Run(() => OpenSettings(null));
        test.Wait();
        test.Run(() => SettingsPage!.Enter(ASSOCIATIONS, UIFormPairs.Value([("*.orig", "csharp")])));
        test.Wait();
        test.Check("settings pairs file: a pair added lands in the file as files_associations { \"*.orig\": \"csharp\" }", () =>
            Editor().GetValueOrDefault(ASSOCIATIONS) is ObjectValue { Properties: { Count: 1 } pairs } && pairs["*.orig"] is StringValue { Value: "csharp" }
            && File.ReadAllText(file()).Contains("\"*.orig\": \"csharp\"", StringComparison.Ordinal));

        // The raw file and back, the { } at the row, the value edited there and saved, the window showing it
        test.Run(() => SettingsPage!.Reveal(ASSOCIATIONS));
        test.WaitFor(() => SettingsPage?.KeyWithKeyboard == ASSOCIATIONS, 2.0f);
        test.Wait();
        test.Run(OpenSettingsFileFromWindow);
        test.Wait();
        test.Check("settings pairs file: the { } at the row opens the file at files_associations", () =>
            document.Path is { } path && PlatformPaths.Same(Path.GetFullPath(path), Path.GetFullPath(file())) && KeyOf(code.Document.LineAt(code.Document.Caret.Line)) == ASSOCIATIONS);
        test.Run(() =>
        {
            code.Document.Replace(code.Text.Replace("\"*.orig\": \"csharp\"", "\"*.orig\": \"rust\"", StringComparison.Ordinal));
            Save();
        });
        test.WaitFor(() => settings.FileAssociations.GetValueOrDefault("*.orig") == "rust", 3.0f);
        test.Run(() => OpenSettings());
        test.WaitFor(() => SettingsPage?.KeyWithKeyboard == ASSOCIATIONS, 2.0f);
        test.Check("settings pairs file: saved in the file, the window shows *.orig = rust and Ctrl+, came back to the row", () =>
            SettingsPage is { } page && page.PairsShown(ASSOCIATIONS)?.Pairs is { } shown && shown.SequenceEqual([("*.orig", "rust")]) && page.KeyWithKeyboard == ASSOCIATIONS);

        test.Run(() => SettingsPage!.Reset(ASSOCIATIONS));
        test.Wait();
        test.Check("settings pairs file: its reset takes it out of the file", () => !Editor().ContainsKey(ASSOCIATIONS) && !SettingsPage!.IsModified!(ASSOCIATIONS));

        // A glob added to files_exclude by the keyboard alone, Down along the lines' buttons to Add Pattern, Enter, typed
        // and Enter again, lands in the file as a list
        const string FILES_EXCLUDE = "files_exclude";
        test.Run(() => SettingsPage!.Reveal(FILES_EXCLUDE));
        test.WaitFor(() => SettingsPage?.KeyWithKeyboard == FILES_EXCLUDE, 2.0f);
        test.Run(() =>
        {
            int buttons = (SettingsPage!.ListShown(FILES_EXCLUDE)?.Items.Count ?? 0) * 2;
            for (int i = 0; i < buttons; i++)
                SettingsPage.Press(UIKeyboard.DOWN);
            SettingsPage.Press(UIKeyboard.ENTER);
        });
        test.Wait();
        test.Check("settings list file: Down to Add Pattern and Enter opens a box at the end", () =>
            SettingsPage!.ListShown(FILES_EXCLUDE) is { Box: { } box } shown && shown.Editing == shown.Items.Count && compositor.Focus == box);
        test.Run(() =>
        {
            if (SettingsPage!.ListShown(FILES_EXCLUDE) is { Box: { } box })
                box.Text = "**/fixtures";
            SettingsPage.Press(UIKeyboard.ENTER);
        });
        test.Wait();
        test.Check("settings list file: and Enter writes files_exclude to the file as a list with **/fixtures at its end", () =>
            Editor().GetValueOrDefault(FILES_EXCLUDE) is ListValue { Items: { Count: > 0 } items } && items[^1] is StringValue { Value: "**/fixtures" }
            && File.ReadAllText(file()).Contains("\"**/fixtures\"", StringComparison.Ordinal) && settings.FilesExclude[^1] == "**/fixtures");
        test.Run(() => SettingsPage!.Reset(FILES_EXCLUDE));
        test.Wait();
        test.Check("settings list file: its reset takes it out of the file", () =>
            !Editor().ContainsKey(FILES_EXCLUDE) && settings.FilesExclude.SequenceEqual(HexSettings.UsualFilesExclude));

        test.Run(() =>
        {
            if (document.Path is { } path && PlatformPaths.Same(Path.GetFullPath(path), Path.GetFullPath(file())))
                Close();
            CloseSettings();
        });
        test.Wait();
    }

    // A settings file's problems agree with what reading it does. A number past an end is said exactly when HexSettings
    // clamps it, for every number of the editor's section, and files associations wants an object of texts
    private void TestSettingsTruthfulProblems(HexPointerScript test)
    {
        static string File(string key, string value) => $"let settings = {{\n    editor: {{\n        {key}: {value}\n    }}\n}};\n";
        static string Written(float number) => number.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        test.Check("settings problems: a number past an end is said exactly when reading the file clamps it, for every number of the editor's", () =>
        {
            string path = Path.Combine(testFolder, "selftest_settings_clamp.hor");
            var wrong = new List<string>();
            foreach (var (key, usual) in HexSettings.Usual)
            {
                if (usual is not NumberValue)
                    continue;

                float[] tried = ClampEnds.TryGetValue(key, out var ends) ? [ends.Least - 1.0f, ends.Most + 1.0f, ends.Most] : [1000.0f, -1000.0f];
                foreach (float value in tried)
                {
                    string text = File(key, Written(value));
                    System.IO.File.WriteAllText(path, text);
                    bool clamped = new HexSettings(path).EditorValues()[key] is NumberValue { Value: var read } && MathF.Abs(read - value) > 0.0001f;
                    bool said = SettingsProblemsIn(path, text, folder: false).Any(problem => problem.Text.Contains(key, StringComparison.Ordinal) && problem.Text.Contains("clamped", StringComparison.Ordinal));
                    if (clamped != said)
                        wrong.Add($"{key}={Written(value)}");
                }
            }

            System.IO.File.Delete(path);
            if (wrong.Count > 0)
                Console.WriteLine($"[Hex self-test] clamp ends out of step: {string.Join(", ", wrong)}");
            return wrong.Count == 0;
        });

        test.Check("settings problems: files_associations as a number is flagged, as an object of texts it isn't", () =>
            SettingsProblemsIn("x", File(ASSOCIATIONS, "42"), folder: false) is [{ } number] && number.Text.Contains(ASSOCIATIONS, StringComparison.Ordinal)
            && SettingsProblemsIn("x", File(ASSOCIATIONS, "{ \"*.orig\": \"csharp\" }"), folder: false).Count == 0);
        test.Check("settings problems: and a pattern whose language isn't a text is flagged too", () =>
            SettingsProblemsIn("x", File(ASSOCIATIONS, "{ \"*.orig\": 5 }"), folder: false) is [{ } pattern] && pattern.Text.Contains("*.orig", StringComparison.Ordinal));
    }
}
