namespace Hex.Dawn;

// The self test, Hex's own dialog for files and folders (UIFileBrowser), which is what Open, Save, Open Folder and an
// extension's pick_file are on every system. A folder of the test's own with a layout, a text, a hidden layout and a
// folder in it, a dialog for each kind shown the way the editor shows them, driven the way the UI drives them.
internal sealed partial class DawnScene
{
    private void TestDialogs(HexPointerScript test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex dialog test");
        string inside = Path.Combine(folder, "inside"), layout = Path.Combine(folder, "a.hor");
        UIFileBrowser? browser = null;

        test.Run(() =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(inside);
            File.WriteAllText(layout, "#define layout\n");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a layout");
            File.WriteAllText(Path.Combine(folder, ".hidden.hor"), "#define layout\n");

            browser = new UIFileBrowser(UIBrowse.Open, HexDialogs.OPEN_TITLE, folder, string.Empty, [("Horizon files", "*.hor")], WorkspaceRoots());
            DialogHost.Present(browser.Window);
        });
        test.Wait();

        test.Check("dialogs: open lists the folders and then every file, nothing hidden, in a window over the editor", () =>
            browser!.Window.Parent is not null && browser.Entries is ["..", "inside/", "a.hor", "notes.txt"] && PlatformPaths.Same(browser.Folder, folder));
        test.Check("dialogs: and only what a filter lets through once one is picked", () =>
        {
            browser!.Filter = "Horizon files";
            bool layouts = browser.Entries is ["..", "inside/", "a.hor"];
            browser.Filter = UIFileBrowser.ALL_FILES;
            return layouts && browser.Entries.Contains("notes.txt");
        });
        test.Check("dialogs: the hidden ones are there when asked for", () =>
        {
            browser!.ShowHidden = true;
            bool shown = browser.Entries.Contains(".hidden.hor");
            browser.ShowHidden = false;
            return shown && !browser.Entries.Contains(".hidden.hor");
        });
        test.Check("dialogs: a folder opened is gone into, .. goes back up, a path typed is gone to", () =>
        {
            browser!.Activate("inside/");
            bool into = PlatformPaths.Same(browser.Folder, inside) && browser.Entries is [".."];
            browser.Activate("..");
            bool back = PlatformPaths.Same(browser.Folder, folder);
            browser.Type("~");
            bool home = PlatformPaths.Same(browser.Folder, PlatformPaths.Home);
            browser.Type(folder);
            return into && back && home && PlatformPaths.Same(browser.Folder, folder);
        });
        test.Check("dialogs: a file that isn't there isn't opened, a file picked and opened is what the dialog hands back and it goes", () =>
        {
            browser!.Name = "missing.hor";
            browser.Press();
            bool refused = !browser.Picked.IsCompleted && browser.Message.Contains("missing.hor");
            browser.Activate("a.hor");
            return refused && browser.Picked is { IsCompletedSuccessfully: true, Result: { } picked } && PlatformPaths.Same(picked, layout) && browser.Window.Parent is null;
        });

        test.Check("dialogs: saving keeps the extension typed, gives a name the filter's when one's picked, and over a file that's there only when Save is pressed again", () =>
        {
            var typed = new UIFileBrowser(UIBrowse.Save, HexDialogs.SAVE_TITLE, folder, "script.d", [("Horizon files", "*.hor")]);
            DialogHost.Present(typed.Window);
            typed.Press();
            bool kept = typed.Picked is { IsCompletedSuccessfully: true, Result: { } script } && PlatformPaths.Same(script, Path.Combine(folder, "script.d"));

            var save = new UIFileBrowser(UIBrowse.Save, HexDialogs.SAVE_TITLE, folder, "fresh", [("Horizon files", "*.hor")]);
            DialogHost.Present(save.Window);
            save.Filter = "Horizon files";
            save.Press();
            bool fresh = kept && save.Picked is { IsCompletedSuccessfully: true, Result: { } made } && PlatformPaths.Same(made, Path.Combine(folder, "fresh.hor"));

            var over = new UIFileBrowser(UIBrowse.Save, HexDialogs.SAVE_TITLE, folder, "a", [("Horizon files", "*.hor")]);
            DialogHost.Present(over.Window);
            over.Filter = "Horizon files";
            over.Press();
            bool asked = !over.Picked.IsCompleted && over.Message.Contains("already");
            over.Press();
            return fresh && asked && over.Picked is { IsCompletedSuccessfully: true, Result: { } replaced } && PlatformPaths.Same(replaced, layout);
        });

        test.Check("dialogs: a folder is picked by being in it, a new one made is gone into, and Cancel hands back nothing", () =>
        {
            var pick = new UIFileBrowser(UIBrowse.Folder, HexDialogs.FOLDER_TITLE, folder, string.Empty, []);
            DialogHost.Present(pick.Window);
            bool foldersOnly = pick.Entries is ["..", "inside/"];
            pick.MakeNewFolder("made here");
            bool made = Directory.Exists(Path.Combine(folder, "made here")) && PlatformPaths.Same(pick.Folder, Path.Combine(folder, "made here"));
            pick.Press();
            bool picked = pick.Picked is { IsCompletedSuccessfully: true, Result: { } chosen } && PlatformPaths.Same(chosen, Path.Combine(folder, "made here"));

            var cancelled = new UIFileBrowser(UIBrowse.Open, HexDialogs.OPEN_TITLE, folder, string.Empty, []);
            DialogHost.Present(cancelled.Window);
            cancelled.Cancel();
            return foldersOnly && made && picked && cancelled.Picked is { IsCompletedSuccessfully: true, Result: null } && cancelled.Window.Parent is null;
        });

        test.Check("dialogs: it has the keyboard, down the list and Enter goes into a folder, Backspace on no name back out to it, Enter takes a file", () =>
        {
            var keys = new UIFileBrowser(UIBrowse.Open, HexDialogs.OPEN_TITLE, folder, string.Empty, [("Horizon files", "*.hor")]);
            DialogHost.Present(keys.Window);
            keys.TakeKeyboard();
            bool has = compositor.Focus is TextBox { Parent: not null } box && Descendants(keys.Window).Contains(box);
            keys.Key(UIKeyboard.DOWN);
            keys.Key(UIKeyboard.DOWN);
            keys.Key(UIKeyboard.ENTER);
            bool into = PlatformPaths.Same(keys.Folder, inside);
            keys.Key(UIKeyboard.BACKSPACE);
            bool back = PlatformPaths.Same(keys.Folder, folder) && keys.Selected == "inside/";
            for (int i = 0; i < keys.Entries.Count && keys.Selected != "a.hor"; i++)
                keys.Key(UIKeyboard.DOWN);
            keys.Key(UIKeyboard.ENTER);
            return has && into && back && keys.Picked is { IsCompletedSuccessfully: true, Result: { } taken } && PlatformPaths.Same(taken, layout)
                && keys.Window.Parent is null;
        });

        test.Check("dialogs: Tab goes round what's in it, its buttons and the filter too, and never out to what's behind", () =>
        {
            var tabbed = new UIFileBrowser(UIBrowse.Open, HexDialogs.OPEN_TITLE, folder, string.Empty, [("Horizon files", "*.hor")]);
            DialogHost.Present(tabbed.Window);
            tabbed.TakeKeyboard();
            bool inside = true, reachedOpen = false, reachedFilter = false;
            for (int i = 0; i < 20; i++)
            {
                compositor.FocusNext();
                inside &= compositor.Focus is { } now && Descendants(tabbed.Window).Contains(now);
                reachedOpen |= compositor.Focus is Button { Label: "Open" };
                reachedFilter |= compositor.Focus is Dropdown;
            }

            tabbed.Cancel();
            return inside && reachedOpen && reachedFilter;
        });

        test.Run(() => Directory.Delete(folder, recursive: true));
    }
}
