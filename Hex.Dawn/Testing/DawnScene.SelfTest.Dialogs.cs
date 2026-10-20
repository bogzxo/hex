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

        test.Check("dialogs: open lists the folders and then what the filter lets through, nothing hidden, in a window over the editor", () =>
            browser!.Window.Parent is not null && browser.Entries is ["..", "inside/", "a.hor"] && PlatformPaths.Same(browser.Folder, folder));
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

        test.Check("dialogs: saving gives a name its extension, and over a file that's there only when Save is pressed again", () =>
        {
            var save = new UIFileBrowser(UIBrowse.Save, HexDialogs.SAVE_TITLE, folder, "fresh", [("Horizon files", "*.hor")]);
            DialogHost.Present(save.Window);
            save.Press();
            bool fresh = save.Picked is { IsCompletedSuccessfully: true, Result: { } made } && PlatformPaths.Same(made, Path.Combine(folder, "fresh.hor"));

            var over = new UIFileBrowser(UIBrowse.Save, HexDialogs.SAVE_TITLE, folder, "a", [("Horizon files", "*.hor")]);
            DialogHost.Present(over.Window);
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

        test.Run(() => Directory.Delete(folder, recursive: true));
    }
}
