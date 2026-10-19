namespace Hex.Dawn;

// The self test, the find bar over the code. Lit as it's typed and counted, the case, whole words and a regex, the
// next and the one before, replacing one at a time and all of them, and escape putting it away.
internal sealed partial class HexScene
{
    private void TestFindBar(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "find_in_me.hor");
        test.Run(() =>
        {
            File.WriteAllText(file, "let moves = {\n    jab: { damage: 5 },\n    Jab_two: { damage: 12 },\n    kick: { damage: 7 }\n};\n");
            Open(file);
            code.Document.MoveToStart(false);
            FindInFile();
            TypeInto(findBox, "jab");
        });
        test.Wait();
        test.Check("find bar: Ctrl+F opens it over the code, what's typed is lit and counted, the first selected", () =>
            FindBarOpen && findBar.Bounds.Max.Y <= code.Bounds.Max.Y + 1 && code.FoundCount == 2 && code.FoundIndex == 0
            && code.Document.SelectedText == "jab" && findCount.Text == "1 of 2");

        test.Run(() => FindStep(forward: true));
        test.Wait();
        test.Check("find bar: enter goes to the next", () => code.FoundIndex == 1 && code.Document.SelectedText == "Jab" && findCount.Text == "2 of 2");

        test.Run(() => findCase.OnPressed!());
        test.Wait();
        test.Check("find bar: the case has to match, one of them goes", () => findCase.Selected && code.FoundCount == 1);

        test.Run(() =>
        {
            findCase.OnPressed!();
            findWord.OnPressed!();
        });
        test.Wait();
        test.Check("find bar: whole words only, Jab_two isn't one", () => !findCase.Selected && findWord.Selected && code.FoundCount == 1);

        test.Run(() =>
        {
            findWord.OnPressed!();
            findRegex.OnPressed!();
            TypeInto(findBox, @"damage: (\d+)");
        });
        test.Wait();
        // Typed into, it looks on from the match it was on, the Jab of Jab_two
        test.Check("find bar: a regex, every damage found", () => findRegex.Selected && code.FoundCount == 3 && code.Document.SelectedText == "damage: 12");

        test.Run(() =>
        {
            ReplaceInFile();
            TypeInto(replaceBox, "damage: $1$1");
            ReplaceOne();
        });
        test.Wait();
        test.Check("find bar: Ctrl+H opens the replace row, and one replaced, the groups in, on to the next", () =>
            replaceRow.Visible && code.Text.Contains("Jab_two: { damage: 1212 }", StringComparison.Ordinal) && code.Document.SelectedText == "damage: 7");

        test.Run(ReplaceEvery);
        test.Wait();
        test.Check("find bar: replace all, the rest of them as one step", () =>
            code.Text.Contains("damage: 55", StringComparison.Ordinal) && code.Text.Contains("damage: 77", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Check("find bar: and one undo puts them back", () =>
            code.Text.Contains("damage: 5 }", StringComparison.Ordinal) && code.Text.Contains("damage: 1212", StringComparison.Ordinal));

        test.Run(() => TypeInto(findBox, "("));
        test.Wait();
        test.Check("find bar: a regex that doesn't work says so", () => code.FoundCount == 0 && findCount.Text == Said("find.bad_regex"));

        test.Run(() =>
        {
            findBox.Focus();
            EscapeFindBar();
        });
        test.Wait();
        test.Check("find bar: escape puts it away, nothing lit, the code with the keys", () =>
            !FindBarOpen && code.FindQuery.Length == 0 && compositor.Focus == code);

        test.Run(() =>
        {
            findOptions = default;
            lastFind = string.Empty;
            if (document.Path == file)
                CloseNow();
        });
        test.Wait();
    }
}
