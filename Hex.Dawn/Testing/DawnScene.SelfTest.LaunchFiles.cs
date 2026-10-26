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
    }
}
