namespace Hex.Dawn;

// The self test, the files a command line names, read the way code reads them (relative, file:12:3, a drive's colon no
// line, a folder on its own the folder), opened in order with the last on screen. And the welcome of a start on a folder
// with no layouts, in code view with no designer round it.
internal sealed partial class DawnScene
{
    private void TestLaunchFiles(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "launch_args");
        string first = Path.Combine(folder, "a.cs"), second = Path.Combine(folder, "sub", "b.cs"), project = Path.Combine(folder, "proj");

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            Directory.CreateDirectory(project);
            System.IO.File.WriteAllText(first, "class A\n{\n    int one;\n}\n");
            System.IO.File.WriteAllText(second, "class B\n{\n    int two;\n    int three;\n}\n");
        });

        test.Check("launch: a path on its own is a file from where Hex was started", () =>
            HexLaunch.Parse(["a.cs"], folder) is ([var only], null) && only == new HexLaunchFile(first, 0, 0));
        test.Check("launch: file:12:3 is the file at that line and column, file:5 at that line", () =>
            HexLaunch.ParseFile("a.cs:12:3", folder) == new HexLaunchFile(first, 12, 3)
            && HexLaunch.ParseFile(Path.Combine("sub", "b.cs") + ":5", folder) == new HexLaunchFile(second, 5, 0));
        test.Check("launch: a drive's colon is no line, C:\\x\\y.cs:5 is y.cs at line 5", () =>
            !OperatingSystem.IsWindows()
            || HexLaunch.ParseFile(@"C:\x\y.cs:5", folder) == new HexLaunchFile(@"C:\x\y.cs", 5, 0)
            && HexLaunch.ParseFile(@"C:\x\y.cs:5:7", folder) == new HexLaunchFile(@"C:\x\y.cs", 5, 7)
            && HexLaunch.ParseFile(@"C:\x\y.cs", folder) == new HexLaunchFile(@"C:\x\y.cs", 0, 0));
        test.Check("launch: a folder on its own is the folder, and the values of options aren't paths", () =>
            HexLaunch.Parse(["--window", "proj", "--size", "1280x720", "--selftest", "launch", "code", "--exit"], folder) is ([], var named)
            && named == project);
        test.Check("launch: two files are both, in their order, with --goto and --open among them", () =>
            HexLaunch.Parse(["a.cs", "--folder", "proj", Path.Combine("sub", "b.cs") + ":2", "-g", "a.cs:3:5", "--open", "gone.cs", "--line", "7"], folder) is (var files, null)
            && files.SequenceEqual([new HexLaunchFile(first, 0, 0), new HexLaunchFile(second, 2, 0), new HexLaunchFile(first, 3, 5), new HexLaunchFile(Path.Combine(folder, "gone.cs"), 7, 0)]));

        // Started as the command line would have it, two files and one that isn't there
        string missing = Path.Combine(folder, "gone.cs");
        test.Run(() => OpenLaunchFiles([new HexLaunchFile(second, 0, 0), new HexLaunchFile(missing, 0, 0), new HexLaunchFile(first, 3, 5)]));
        test.Wait();
        test.Check("launch: two files named open both, the last on screen at its line and column", () =>
            documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, second)) && document.Path is { } shown && PlatformPaths.Same(shown, first)
            && code.Document.Caret.Line == 2 && code.Document.Caret.Column == 4);
        test.Check("launch: a file that isn't there is said in a card that goes by itself, not opened", () =>
            !documents.Any(open => open.Path is { } path && PlatformPaths.Same(path, missing))
            && toasts.Cards.Any(card => card.Text.Equals(Said("launch.missing", ("files", missing)), StringComparison.OrdinalIgnoreCase) && card.Seconds > 0.0f));

        test.Run(() =>
        {
            foreach (var card in toasts.Cards.Where(card => card.Text.Contains(missing, StringComparison.Ordinal)))
                card.Close();
            foreach (HexDocument open in documents.Where(open => open.Path is { } path && PlatformPaths.IsUnder(path, folder)).ToList())
                CloseDocument(open);
        });
        test.Wait();
    }

    private void TestPlainWelcome(HexPointerScript test)
    {
        string ofCode = Path.Combine(testFolder, "launch_code"), layouts = Path.Combine(testFolder, "launch_layouts");
        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(ofCode, "src"));
            Directory.CreateDirectory(Path.Combine(ofCode, "node_modules", "ui"));
            Directory.CreateDirectory(Path.Combine(layouts, "ui"));
            System.IO.File.WriteAllText(Path.Combine(ofCode, "src", "main.go"), "package main\n");
            System.IO.File.WriteAllText(Path.Combine(ofCode, "node_modules", "ui", "skip.hor"), "let skip = 1;\n");
            System.IO.File.WriteAllText(Path.Combine(layouts, "ui", "menu.hor"), "#define layout\n");
        });
        test.Check("launch: a folder with no layouts near its top starts on code, one with them and nothing at all as they were", () =>
            !HasLayoutsNear([ofCode]) && HasLayoutsNear([layouts]) && HasLayoutsNear([ofCode, layouts]) && !HasLayoutsNear([]) && !WantsPlainWelcome());

        // The welcome as a start on code has it, every tab closed and the empty layout left
        test.Run(() =>
        {
            welcomeWanted = plainWelcome = true;
            Show(NewDocument());
            CloseAll();
        });
        test.Settle(0.3f);
        test.Check("launch: started on code the welcome is up in code view, with no Design, Code and Play over it and no Properties", () =>
            plainPage is { Visible: true } page && page.Parent == codeScroll && codeScroll.Children[^1] == page && welcome is not { Visible: true }
            && document == startedWith && document.View == HexView.Code && !LayoutTools
            && !toolbar.Visible && codeScroll.Visible && !canvas.Visible && !right.Shown);
        test.Run(() => Shot("plain-welcome"));
        test.Settle(0.3f);
        test.Run(() =>
        {
            welcomeWanted = plainWelcome = false;
            document.View = HexView.Design;
            ApplyView();
        });
        test.Settle(0.3f);
        test.Check("launch: and the designer as ever once it isn't, the toolbar back", () =>
            plainPage is not { Visible: true } && toolbar.Visible && LayoutTools && right.Shown == !settings.PropertiesHidden);

        TestPlainStart(test, Path.Combine(ofCode, "src", "main.go"));
        TestRunButton(test);
    }

    // The plain start as a Go folder has it, no Untitled tab, no HIDL and Ln 1 in the status bar, New File first and New
    // Layout last, and the empty layout gone for good once main.go opens
    private void TestPlainStart(HexPointerScript test, string goFile)
    {
        // A button of the layout says its text by key, "@welcome.open_file", the one made in code says it
        bool Says(Button button, string key) => button.Label == Said(key) || button.Label == "@" + key;
        int IndexIn(UIComponent list, string key) => list.Children.ToList().FindIndex(child => child is Button { } button && Says(button, key));
        test.Run(() =>
        {
            welcomeWanted = plainWelcome = true;
            Show(NewDocument());
            CloseAll();
        });
        test.Settle(0.3f);
        test.Check("launch: started on code the empty layout has no tab, and the status bar says nothing of HIDL or where the caret is", () =>
            PlainStart && documents.Count == 1 && docTabs.Tabs.Count == 0
            && languageItem.IsVisible?.Invoke() == false && caretItem.IsVisible?.Invoke() == false);
        test.Check("launch: its Start list is New File, Open File, Open Folder, the palette and New Layout last", () =>
            plainPage is not null && Descendant<Button>(plainPage, button => Says(button, "welcome.new_file"))?.Parent is { } start
            && IndexIn(start, "welcome.new_file") < IndexIn(start, "welcome.open_file")
            && IndexIn(start, "welcome.open_file") < IndexIn(start, "welcome.open_folder")
            && IndexIn(start, "welcome.commands") < IndexIn(start, "welcome.new_layout")
            && IndexIn(start, "welcome.new_layout") < IndexIn(start, "welcome.keys"));

        test.Run(() => Open(goFile));
        test.Settle(0.3f);
        test.Check("launch: opening main.go leaves the one tab, the empty layout closed and not hidden", () =>
            documents.Count == 1 && document.Path is { } open && PlatformPaths.Same(open, goFile) && docTabs.Tabs.Count == 1
            && !PlainStart && languageItem.IsVisible?.Invoke() == true);

        // Back to the designer's welcome the way every other test has it
        test.Run(() =>
        {
            CloseDocument(document);
            plainWelcome = false;
            document.View = HexView.Design;
            ApplyView();
        });
        test.Settle(0.3f);
        test.Check("launch: and the designer's welcome keeps New Layout first with New File after it", () =>
            welcome is { Visible: true } && Descendant<Button>(welcome, button => Says(button, "welcome.new_layout"))?.Parent is { } start
            && IndexIn(start, "welcome.new_layout") < IndexIn(start, "welcome.new_file")
            && IndexIn(start, "welcome.new_file") < IndexIn(start, "welcome.open_file"));
        test.Run(() => welcomeWanted = false);
    }

    // Run only over what has something to run, a layout plays, a .go has a preset, a log or markdown has nothing
    private void TestRunButton(HexPointerScript test)
    {
        string folder = Path.Combine(testFolder, "launch_run");
        string log = Path.Combine(folder, "app.log"), rej = Path.Combine(folder, "fix.rej"), md = Path.Combine(folder, "notes.md"), go = Path.Combine(folder, "main.go");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(log, "started\n");
            System.IO.File.WriteAllText(rej, "@@ -1 +1 @@\n");
            System.IO.File.WriteAllText(md, "# notes\n");
            System.IO.File.WriteAllText(go, "package main\n");
        });
        test.Check("launch: Run is there for a layout, and for a .go Hex can set a run up for", () =>
            Runnable(startedWith ?? document) && OpenBehindFor(go) is { } goDocument && Runnable(goDocument));
        test.Check("launch: and not for a .log, a .rej or markdown, which have nothing to run", () =>
            OpenBehindFor(log) is { } logDocument && !Runnable(logDocument) && OpenBehindFor(rej) is { } rejDocument && !Runnable(rejDocument)
            && OpenBehindFor(md) is { } mdDocument && !Runnable(mdDocument));
        test.Run(() => Open(log));
        test.Settle(0.2f);
        test.Check("launch: the toolbar's Run is hidden with the log on screen", () => !layout.Get<Button>("play_button").Visible);
        test.Run(() =>
        {
            foreach (HexDocument open in documents.Where(open => open.Path is { } path && PlatformPaths.IsUnder(path, folder)).ToList())
                CloseDocument(open);
        });
        test.Settle(0.2f);
    }

    // Helper method to open a file as text behind the one on screen for a check, the document it has
    private HexDocument? OpenBehindFor(string path) => ((IDocuments)this).OpenBehind(path);

    // Helper method to find a component inside another one, the first that's the kind and passes
    private static T? Descendant<T>(UIComponent inside, Func<T, bool> passes) where T : UIComponent
    {
        foreach (UIComponent child in inside.Children)
        {
            if (child is T found && passes(found))
                return found;
            if (Descendant(child, passes) is { } deeper)
                return deeper;
        }

        return null;
    }
}
