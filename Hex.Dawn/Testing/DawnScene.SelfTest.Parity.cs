using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test, the every day keys of VS Code. Ctrl+/ in each language's own comment, Shift+Alt+A a block comment,
// Shift+Alt+Down a line copied under itself, Ctrl+L the line selected, Ctrl+Shift+L every place the word is, and the
// explorer showing dot files and dot folders but not .git.
internal sealed partial class DawnScene
{
    private void TestParity(HexPointerScript test)
    {
        TestParityComments(test);
        TestParityLineKeys(test);
        TestParityDotFiles(test);
    }

    private void TestParityComments(HexPointerScript test)
    {
        // A file of each, its first line commented with Ctrl+/ and what it comes to
        (string Name, string Text, string Commented)[] languages =
        [
            ("parity.py", "x = 1\n", "# x = 1"),
            ("parity.sql", "select 1;\n", "-- select 1;"),
            ("parity.html", "<p>hi</p>\n", "<!-- <p>hi</p> -->"),
            ("parity.cs", "int x = 1;\n", "// int x = 1;")
        ];

        foreach (var (name, text, commented) in languages)
        {
            string file = Path.Combine(testFolder, name);
            test.Run(() =>
            {
                File.WriteAllText(file, text);
                Open(file);
                code.GoTo(0, 0);
                code.Document.ToggleComment();
            });
            test.Wait();
            test.Check($"parity: Ctrl+/ in {Path.GetExtension(name)} writes {commented}", () => code.Document.LineAt(0) == commented);
            test.Run(() => code.Document.ToggleComment());
            test.Check($"parity: and again in {Path.GetExtension(name)} takes it back off", () => code.Document.LineAt(0) == text.TrimEnd('\n'));

            // The .cs stays open for the block comment
            if (Path.GetExtension(name) != ".cs")
                CloseTestFile(test, file);
        }

        // Still in the .cs, the x put in a block comment and taken back out
        test.Run(() =>
        {
            code.Document.Select(new UITextPosition(0, 4), new UITextPosition(0, 5));
            code.Document.ToggleBlockComment();
        });
        test.Check("parity: Shift+Alt+A puts the selection in a block comment in .cs", () => code.Document.LineAt(0) == "int /* x */ = 1;" && code.Document.SelectedText == "x");
        test.Run(() => code.Document.ToggleBlockComment());
        test.Check("parity: and again takes it back out", () => code.Document.LineAt(0) == "int x = 1;");
        CloseTestFile(test, Path.Combine(testFolder, "parity.cs"));

        test.Check("parity: the comment commands are in Edit on VS Code's keys", () =>
            CommandKey("code.toggle_comment") == "Ctrl+/" && CommandKey("code.toggle_block_comment") == "Shift+Alt+A"
            && UIKeyboard.Meaning(Key.A, primary: false, shift: true, alt: true, mac: false).Code == UIKeyboard.TOGGLE_BLOCK_COMMENT);
    }

    private void TestParityLineKeys(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "parity_lines.cs");
        test.Run(() =>
        {
            File.WriteAllText(file, "int a = 1;\nhit x hit;\nhit\n");
            Open(file);
            code.GoTo(0, 2);
            code.Document.CopyLines(1);
        });
        test.Wait();
        test.Check("parity: Shift+Alt+Down copies the line under itself, the caret going with the copy", () =>
            code.Text.StartsWith("int a = 1;\nint a = 1;\nhit", StringComparison.Ordinal) && code.Document.Caret == new UITextPosition(1, 2));

        test.Run(() =>
        {
            code.Document.Undo();
            code.GoTo(0, 2);
            code.Document.ExpandLineSelection();
        });
        test.Check("parity: Ctrl+L selects the caret's line", () => code.Document.SelectedText == "int a = 1;\n");
        test.Run(() => code.Document.ExpandLineSelection());
        test.Check("parity: and again the next one with it", () => code.Document.SelectedText == "int a = 1;\nhit x hit;\n");

        test.Run(() =>
        {
            code.GoTo(1, 1);
            code.Document.SelectAllOccurrences();
        });
        test.Check("parity: Ctrl+Shift+L selects every place the word is", () =>
            code.Document.CaretCount == 3 && code.Document.SelectedTextOfEvery() == "hit\nhit\nhit");
        test.Run(() => code.Document.CollapseCarets());

        // The keys as the keyboard reads them, and the commands of the Selection menu showing them. Ctrl+Alt+Up and Down
        // still add a caret, the old Ctrl+Shift+L is Shift+Alt+I
        test.Check("parity: the line keys are VS Code's, in the keyboard and the Selection menu", () =>
            UIKeyboard.Meaning(Key.Down, primary: false, shift: true, alt: true, mac: false).Code == UIKeyboard.COPY_LINES_DOWN
            && UIKeyboard.Meaning(Key.Up, primary: false, shift: true, alt: true, mac: false).Code == UIKeyboard.COPY_LINES_UP
            && UIKeyboard.Meaning(Key.L, primary: true, shift: false, alt: false, mac: false).Code == UIKeyboard.SELECT_LINE
            && UIKeyboard.Meaning(Key.L, primary: true, shift: true, alt: false, mac: false).Code == UIKeyboard.SELECT_ALL_OCCURRENCES
            && UIKeyboard.Meaning(Key.Down, primary: true, shift: false, alt: true, mac: false).Code == UIKeyboard.CARET_BELOW
            && CommandKey("code.copy_line_down") == "Shift+Alt+Down" && CommandKey("code.copy_line_up") == "Shift+Alt+Up"
            && CommandKey("code.select_line") == "Ctrl+L" && CommandKey("code.select_all_occurrences") == "Ctrl+Shift+L"
            && CommandKey("code.add_cursors_line_ends") == "Shift+Alt+I" && CommandKey("code.add_cursor_below") == "Ctrl+Alt+Down"
            && CommandKey("code.duplicate_line") == "Ctrl+Shift+D");

        CloseTestFile(test, file);
    }

    // The file a check opened closed again, as long as it's the one on screen, its changes thrown away
    private void CloseTestFile(HexPointerScript test, string file)
    {
        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }

    private void TestParityDotFiles(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "parity_dots");
        bool browsingWas = false;
        bool Listed(string name) => explorer.explorerRows.Any(row => row.Path == Path.Combine(folder, name));

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(Path.Combine(folder, ".github", "workflows"));
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
            Directory.CreateDirectory(Path.Combine(folder, "src"));
            File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\n");
            File.WriteAllText(Path.Combine(folder, ".DS_Store"), string.Empty);
            File.WriteAllText(Path.Combine(folder, "main.cs"), "class A {}\n");

            browsingWas = browsing;
            explorer.AddFolder(folder);
        });
        test.WaitFor(() => Listed("main.cs"), 3.0f);
        test.Check("parity: the explorer lists .github and .gitignore", () => Listed(".github") && Listed(".gitignore") && Listed("src"));
        test.Check("parity: but not .git, nor .DS_Store", () => !Listed(".git") && !Listed(".DS_Store"));

        test.Run(() =>
        {
            explorer.RemoveFolder(folder);
            browsing = browsingWas;
        });
        test.Wait();
    }

    // The key a command of the menus shows, by the text of its label
    private string? CommandKey(string said) => Commands().FirstOrDefault(pair => LabelOf(pair.Item) == Said(said).TrimEnd('.')).Item?.Shortcut;
}
