using System.Text;

using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The self test of the search view in the side bar. Ctrl+Shift+F opening it with no dialog and its box given the keys,
// results coming in as it's typed with an older answer dropped, every match of a line a row of its own, a row clicked
// going there, the toggles shared with the find bar, a regex that doesn't build saying why, the files to include and
// to exclude, dismissing a match and a file, and Replace in Files on the disk and in an open file as one step to undo.
internal sealed partial class DawnScene
{
    private void TestSearchView(HexPointerScript test)
    {
        string root = string.Empty, three = string.Empty, crlf = string.Empty, notes = string.Empty, opened = string.Empty;
        bool browsingWas = false, waitedFirst = false;
        string Text(string path) => File.ReadAllText(path);
        UITreeItem? FileRow(string path) => sideTree.Items.Find(item => item.Tooltip == path);
        bool Listed(Func<string, bool> file) => results is { Count: > 0 } found && found.All(result => file(Path.GetRelativePath(root, result.Path).Replace('\\', '/')));
        bool Found(string query, int count) => resultsFound is { } asked && asked.Query == query && results?.Count == count && searchError is null;

        test.Run(() =>
        {
            root = Path.Combine(testFolder, "search");
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            Directory.CreateDirectory(Path.Combine(root, "src"));

            three = Path.Combine(root, "src", "three.cs");
            crlf = Path.Combine(root, "src", "crlf.cs");
            notes = Path.Combine(root, "notes.md");
            opened = Path.Combine(root, "opened.txt");
            File.WriteAllText(three, "int quokka = quokka + quokka;\n");
            File.WriteAllBytes(crlf, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("// quokka here\r\nquokka();\r\n")]);
            File.WriteAllText(notes, "QUOKKA and quokka\n");
            File.WriteAllText(opened, "quokka bar\n");

            browsingWas = browsing;
            explorer.AddFolder(root);
            codeEditing.findOptions = default;
        });
        test.Wait();

        // Ctrl+Shift+F as the keys press it
        test.Run(() => commands.Keys.Update(key => key == Key.F, control: true, shift: true, alt: false, typing: false));
        test.Wait();
        test.Check("search: Ctrl+Shift+F shows the side bar's search view with its box given the keys, no dialog", () =>
            chrome.Dialog is null && textDialog is not { IsOpen: true } && Side == HexSide.Search && searchView is { Visible: true }
            && compositor.Focus == searchQuery && searchCase.Visible && searchWord.Visible && searchRegex.Visible && !searchReplaceRow.Visible && !searchScope.Visible);

        test.Run(() =>
        {
            TypeInto(searchQuery, "quokka");
            waitedFirst = searchWait > 0.0f && resultsFound is null;
        });
        test.WaitFor(() => Found("quokka", 8), 5.0f);
        test.Check("search: what's typed is looked for a moment later, without enter", () => waitedFirst && Found("quokka", 8));
        test.Check("search: a line with three matches is three rows, the file's row says 3, the line under the boxes the results and files", () =>
            FileRow(three) is { Children: { Count: 3 } rows, Detail: "3" } && rows.Select(row => row.Mark.Start).SequenceEqual([4, 13, 22])
            && searchMessage.Text == "8 results in 4 files");

        // Two searches one after the other, the first one's answer is dropped whenever it comes
        test.Run(() =>
        {
            searchQuery.Text = "and";
            SearchNow();
            searchQuery.Text = "quokka";
            SearchNow();
        });
        test.Settle(0.5f);
        test.Check("search: of two searches asked for one after the other only the newest is shown", () => Found("quokka", 8));

        test.Click(TreeRow(() => FileRow(three)?.Children?[1]));
        test.Wait();
        test.Check("search: a row clicked opens the file at that match", () =>
            document.Path is { } path && Path.GetFullPath(path) == three && code.Document.Caret.Line == 0 && code.Document.Caret.Column == 13);

        /* The toggles, the find bar's own */

        test.Run(() => searchCase.OnPressed!());
        test.WaitFor(() => Found("quokka", 7), 5.0f);
        test.Check("search: the case toggle looks again at once, QUOKKA goes, and it's the find bar's toggle too", () =>
            Found("quokka", 7) && searchCase.Selected && codeEditing.findOptions.MatchCase && codeEditing.findCase.Selected);
        test.Run(() => codeEditing.findOptions = codeEditing.findOptions with { MatchCase = false });
        test.WaitFor(() => Found("quokka", 8), 5.0f);
        test.Check("search: the find bar's toggle changed, the view follows and looks again", () => Found("quokka", 8) && !searchCase.Selected);

        test.Run(() =>
        {
            searchRegex.OnPressed!();
            TypeInto(searchQuery, "quok(");
        });
        test.WaitFor(() => searchError is not null, 5.0f);
        test.Check("search: a regex that doesn't build says why in red under the box, and nothing is listed", () =>
            searchError is not null && searchMessage.Text.StartsWith("Invalid regular expression", StringComparison.Ordinal) && searchMessage.Color == ErrorColor
            && results is { Count: 0 } && sideTree.Items.Count == 0);
        test.Run(() => TypeInto(searchQuery, @"quok+a\b"));
        test.WaitFor(() => Found(@"quok+a\b", 8), 5.0f);
        test.Check("search: and a regex that does finds them again", () => Found(@"quok+a\b", 8) && searchMessage.Color != ErrorColor);
        test.Run(() =>
        {
            searchRegex.OnPressed!();
            TypeInto(searchQuery, "quokka");
        });
        test.WaitFor(() => Found("quokka", 8), 5.0f);

        /* The files to include and to exclude */

        test.Run(() =>
        {
            searchMore.OnPressed!();
            TypeInto(searchInclude, "src/**");
        });
        test.WaitFor(() => results?.Count == 5, 5.0f);
        test.Check("search: the dots open the files to include, src/** keeps to src", () => searchScope.Visible && results?.Count == 5 && Listed(file => file.StartsWith("src/")));
        test.Run(() =>
        {
            TypeInto(searchInclude, string.Empty);
            TypeInto(searchExclude, "*.md");
        });
        test.WaitFor(() => results?.Count == 6, 5.0f);
        test.Check("search: the files to exclude, *.md leaves the notes out", () => results?.Count == 6 && Listed(file => !file.EndsWith(".md")));
        test.Run(() => TypeInto(searchExclude, string.Empty));
        test.WaitFor(() => Found("quokka", 8), 5.0f);

        /* Dismissing, from the list and not the disk */

        UITreeItem? dismissed = null;
        test.Run(() => dismissed = FileRow(three)?.Children?[0]);
        test.Hover(TreeRow(() => dismissed));
        test.Settle(0.2f);
        test.Click(() => sideTree.ActionBounds(dismissed!, 0) is { } bounds && sideTree.Module is { } module ? module.ToWorld(bounds.Center) : test.Pointer.Position);
        test.Wait();
        test.Check("search: the X of a hovered match dismisses it, the file's row says 2, the file is as it was", () =>
            results?.Count == 7 && FileRow(three) is { Detail: "2" } && Text(three) == "int quokka = quokka + quokka;\n");
        test.Run(() => FileRow(notes)!.Actions![0].Pressed());
        test.Wait();
        test.Check("search: the X of a file's row dismisses every match of it", () => results?.Count == 5 && FileRow(notes) is null && File.Exists(notes));

        /* Replace in Files */

        test.Run(() =>
        {
            Open(opened);
            ReplaceInFiles();
            TypeInto(searchQuery, "quokka");
            TypeInto(searchReplace, "wombat");
        });
        test.WaitFor(() => Found("quokka", 8), 5.0f);
        test.Check("search: Ctrl+Shift+H opens the replace row, and each row shows what it turns into", () =>
            searchReplaceRow.Visible && FileRow(three)?.Children?[0] is { Text: "int quokkawombat = quokka + quokka;", Lit: [(10, 6)] });
        // The view with every row of it open, for a look with HEX_SELFTEST_SHOTS set
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "search_view.png"));
        });
        // The picture is written off the render thread, a run ending straight after it left an empty file
        test.Settle(System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } ? 2.0f : 0.3f);
        test.Run(ReplaceAllInFiles);
        test.Wait();
        test.Check("search: Replace All asks once with how many matches in how many files", () =>
            chrome.Dialog is { } asked && asked.Buttons.Count == 2 && Descendants(asked).OfType<Label>().Any(label => label.Text == "Replace 8 matches in 4 files with 'wombat'?"));
        test.Settle(0.4f);
        test.Click(() => chrome.Dialog is { } asked ? chrome.ToWorld(asked.Buttons[0].Bounds.Center) : test.Pointer.Position);
        test.WaitFor(() => chrome.Dialog is null && Found("quokka", 0), 5.0f);
        test.Check("search: closed files are written, the BOM and the CRLFs kept, and nothing is left to list", () =>
            Text(notes) == "wombat and wombat\n"
            && File.ReadAllBytes(crlf).SequenceEqual((byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("// wombat here\r\nwombat();\r\n")]));
        test.Check("search: the open files are changed in their editors and not saved, the one behind as well as the one on screen", () =>
            OpenDocument(opened) is { Modified: true } open && open.Editor.Text == "wombat bar\n" && Text(opened) == "quokka bar\n"
            && OpenDocument(three) is { Modified: true } behind && behind.Editor.Text == "int wombat = wombat + wombat;\n" && Text(three) == "int quokka = quokka + quokka;\n");
        test.Run(() => OpenDocument(opened)!.Editor.Document.Undo());
        test.Wait();
        test.Check("search: and one undo puts the open file back", () => OpenDocument(opened)?.Editor.Text == "quokka bar\n");

        /* The references of a name, in the same tree */

        test.Run(() =>
        {
            code.Document.MoveTo(new UITextPosition(0, 1));
            codeEditing.FindReferences();
        });
        test.Wait();
        test.Check("search: Shift+F12 lists the references in the same tree with the boxes empty", () =>
            Side == HexSide.Search && resultsFound is null && searchQuery.Text.Length == 0 && results is { Count: > 0 } && sideTree.Items.Count > 0);

        test.Run(() =>
        {
            ShowSearchReplace(false);
            ShowSearchScope(false);
            searchReplace.Text = string.Empty;
            CloseResults();
            codeEditing.findOptions = default;
            foreach (string path in new[] { opened, three })
            {
                if (OpenDocument(path) is not { } open)
                    continue;
                Show(open);
                CloseNow();
            }
            explorer.RemoveFolder(root);
            browsing = browsingWas;
        });
        test.Wait();
    }
}
