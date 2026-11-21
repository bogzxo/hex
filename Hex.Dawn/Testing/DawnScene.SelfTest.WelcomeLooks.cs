using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The welcome page in every look Hex comes with at 100, 150 and 200%, the first time (nothing worked on yet) and once
// there's history. Everything on it inside the page, nothing over anything else. HEX_SELFTEST_SHOTS names a folder for a
// picture of each.
internal sealed partial class DawnScene
{
    private void TestWelcomeLooks(HexPointerScript test)
    {
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        float scaleBefore = 1.0f;
        string[] kept = [];
        string[] files = [.. Enumerable.Range(0, 4).Select(i => Path.Combine(testFolder, $"welcome-looks-{i}.cs"))];

        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            kept = [.. recent];
            foreach (string file in files)
                File.WriteAllText(file, "// the welcome's looks\n");
            testRecent = true;
            ShowFace(HexFace.Dawn);
            welcomeWanted = true;
            plainWelcome = false;
            Show(NewDocument());
            CloseAll();
        });
        test.Settle(0.5f);

        // Every button and label of the page, shown, inside the page's own width
        bool Inside(Panel page)
        {
            UIComponent? outside = Descendants(page).FirstOrDefault(part => part.VisibleInHierarchy && part is Button or Label && part.Bounds.Width > 0
                && (part.Bounds.Min.X < page.Bounds.Min.X - 1 || part.Bounds.Max.X > page.Bounds.Max.X + 1));
            if (outside is not null)
                Console.WriteLine($"[Hex self-test]   outside the page {page.Bounds}: {(outside as Button)?.Label ?? (outside as Label)?.Text} at {outside.Bounds}");
            return outside is null;
        }

        foreach (bool history in (bool[])[false, true])
        {
            foreach (HexLook look in HexLooks.BuiltIn)
            {
                foreach (float scale in (float[])[1.0f, 1.5f, 2.0f])
                {
                    string named = $"{look.Name} at {scale * 100:0}%{(history ? " with history" : " the first time")}";
                    test.Run(() =>
                    {
                        PickLook(look);
                        SetInterfaceSize(scale, remember: false);
                        if (history && recent.Count == 0)
                        {
                            foreach (string file in files)
                                Remember(file);
                        }
                        else if (!history && recent.Count > 0)
                            ForgetRecent();
                    });
                    test.Run(() => Engine.WindowManager.RequestFrames());
                    test.Settle(0.5f);
                    test.Check($"welcome looks: {named}, the page is up", () => welcome is { Visible: true, Bounds.Width: > 0 });
                    test.Check($"welcome looks: {named}, everything on it is inside it", () => welcome is { } page && Inside(page));
                    // The first time Open Folder leads, with no empty Recent; once there's history the files lead instead
                    test.Check($"welcome looks: {named}, {(history ? "the files worked on last lead and Open Folder is quiet" : "Open Folder leads and there's no empty Recent")}", () =>
                        welcome is { } page && welcomeSizes.FirstOrDefault(each => Descendants(page).Contains(each.OpenFolder)) is { } sizing
                        && sizing.RecentColumn.Visible == history && sizing.OpenFolder.Primary == !history
                        && (!history || (sizing.List.Children.OfType<Button>().Count() == files.Length
                            && sizing.RecentColumn.Bounds.Max.Y >= sizing.OpenFolder.Bounds.Max.Y)));
                    string shot = $"welcome-{(history ? "history" : "first")}-{look.Name.Replace(' ', '-').ToLowerInvariant()}-{scale * 100:0}";
                    // A changed look or scale takes a few frames to be drawn at, the picture asked for after them
                    test.Run(() => Engine.WindowManager.RequestFrames());
                    test.Settle(0.5f);
                    test.Run(() =>
                    {
                        Engine.WindowManager.RequestFrames();
                        if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                            Engine.CaptureScreenshot(Path.Combine(shots, shot + ".png"));
                    });
                    test.Settle(0.5f);
                }
            }
        }

        // A file's row opens it with a click on it, the folder's words over the row taking nothing from it
        Button? firstFile = null;
        test.Run(() =>
        {
            SetInterfaceSize(1.0f, remember: false);
            PickLook(HexLooks.Solstice);
        });
        test.Settle(0.5f);
        test.Run(() => firstFile = welcome is { } page ? welcomeSizes.FirstOrDefault(each => Descendants(page).Contains(each.OpenFolder))?.List.Children.OfType<Button>().FirstOrDefault() : null);
        test.Click(() => firstFile is { } row ? chrome.ToWorld(row.Children[0] is Label { Visible: true } where ? where.Bounds.Center : row.Bounds.Center) : default);
        test.Settle(0.4f);
        test.Check("welcome looks: a click on a file's row, on the folder's words even, opens it", () =>
            firstFile is { Tooltip: var path } && document.Path is { } open && PlatformPaths.Same(open, path));
        test.Run(() => CloseAll());
        test.Settle(0.3f);

        test.Run(() =>
        {
            testRecent = false;
            recent.Clear();
            recent.AddRange(kept);
            WriteRecent();
            welcomeWanted = false;
            SetInterfaceSize(scaleBefore, remember: false);
            UseEditorSkin(skinBefore, themeBefore, save: false);
            foreach (string file in files)
                try { File.Delete(file); } catch (IOException) { }
        });
        test.Settle(0.3f);
    }
}
