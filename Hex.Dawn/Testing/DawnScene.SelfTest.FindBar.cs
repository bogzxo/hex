namespace Hex.Dawn;

// The self test, the find bar over the code. Lit as it's typed and counted, the case, whole words and a regex, the
// next and the one before, replacing one at a time and all of them, and escape putting it away.
internal sealed partial class DawnScene
{
    private void TestFindBar(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "find_in_me.hor");
        test.Run(() =>
        {
            File.WriteAllText(file, "let moves = {\n    jab: { damage: 5 },\n    Jab_two: { damage: 12 },\n    kick: { damage: 7 }\n};\n");
            Open(file);
            code.Document.MoveToStart(false);
            codeEditing.FindInFile();
            TypeInto(codeEditing.findBox, "jab");
        });
        test.Wait();
        test.Check("find bar: Ctrl+F opens it over the code, what's typed is lit and counted, the first selected", () =>
            codeEditing.FindBarOpen && codeEditing.findBar.Bounds.Max.Y <= code.Bounds.Max.Y + 1 && code.FoundCount == 2 && code.FoundIndex == 0
            && code.Document.SelectedText == "jab" && codeEditing.findCount.Text == "1 of 2");

        test.Run(() => codeEditing.FindStep(forward: true));
        test.Wait();
        test.Check("find bar: enter goes to the next", () => code.FoundIndex == 1 && code.Document.SelectedText == "Jab" && codeEditing.findCount.Text == "2 of 2");

        test.Run(() => codeEditing.findCase.OnPressed!());
        test.Wait();
        test.Check("find bar: the case has to match, one of them goes", () => codeEditing.findCase.Selected && code.FoundCount == 1);

        test.Run(() =>
        {
            codeEditing.findCase.OnPressed!();
            codeEditing.findWord.OnPressed!();
        });
        test.Wait();
        test.Check("find bar: whole words only, Jab_two isn't one", () => !codeEditing.findCase.Selected && codeEditing.findWord.Selected && code.FoundCount == 1);

        test.Run(() =>
        {
            codeEditing.findWord.OnPressed!();
            codeEditing.findRegex.OnPressed!();
            TypeInto(codeEditing.findBox, @"damage: (\d+)");
        });
        test.Wait();
        // Typed into, it looks on from the match it was on, the Jab of Jab_two
        test.Check("find bar: a regex, every damage found", () => codeEditing.findRegex.Selected && code.FoundCount == 3 && code.Document.SelectedText == "damage: 12");

        test.Run(() =>
        {
            codeEditing.ReplaceInFile();
            TypeInto(codeEditing.replaceBox, "damage: $1$1");
            codeEditing.ReplaceOne();
        });
        test.Wait();
        test.Check("find bar: Ctrl+H opens the replace row, and one replaced, the groups in, on to the next", () =>
            codeEditing.replaceRow.Visible && code.Text.Contains("Jab_two: { damage: 1212 }", StringComparison.Ordinal) && code.Document.SelectedText == "damage: 7");

        test.Run(codeEditing.ReplaceEvery);
        test.Wait();
        test.Check("find bar: replace all, the rest of them as one step", () =>
            code.Text.Contains("damage: 55", StringComparison.Ordinal) && code.Text.Contains("damage: 77", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Check("find bar: and one undo puts them back", () =>
            code.Text.Contains("damage: 5 }", StringComparison.Ordinal) && code.Text.Contains("damage: 1212", StringComparison.Ordinal));

        // Tab goes from the find box to the replace box, then to the toggles, VS Code's order, and back the same way
        test.Run(() => codeEditing.findBox.Focus());
        PressKeys(test, "Tab");
        test.Check("find bar: Tab from the find box is the replace box", () => compositor.Focus == codeEditing.replaceBox);
        PressKeys(test, "Tab");
        test.Check("find bar: and from there the case toggle", () => compositor.Focus == codeEditing.findCase);
        PressKeys(test, "Shift+Tab Shift+Tab");
        test.Check("find bar: Shift+Tab goes back through the replace box to the find box", () => compositor.Focus == codeEditing.findBox);

        test.Run(() => TypeInto(codeEditing.findBox, "("));
        test.Wait();
        test.Check("find bar: a regex that doesn't work says so", () => code.FoundCount == 0 && codeEditing.findCount.Text == Said("find.bad_regex"));

        test.Run(() =>
        {
            codeEditing.findBox.Focus();
            codeEditing.EscapeFindBar();
        });
        test.Wait();
        test.Check("find bar: escape puts it away, nothing lit, the code with the keys", () =>
            !codeEditing.FindBarOpen && code.FindQuery.Length == 0 && compositor.Focus == code);

        test.Run(() =>
        {
            codeEditing.findOptions = default;
            codeEditing.lastFind = string.Empty;
            if (document.Path == file)
                CloseNow();
        });
        test.Wait();
    }
}
