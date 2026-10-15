using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// The self test, saving, opening and tabs, the editor's own layout, scrolling, its size and the layout debugger.
internal sealed partial class HexScene
{
    private void TestSavingAndOpening(HexPointerScript test)
    {
        /* Saving, tabs, opening */

        string saved = string.Empty;

        test.Click(Part("name_box"));
        test.Run(() => TypeInto(nameBox, "selftest_menu"));
        test.Wait();
        test.Check("name: the tab goes by the name of the layout, and so does the window", () =>
            docTabs.Tabs.Count == 1 && docTabs.Tabs[0].Title == "selftest_menu" && ((TitleBar)top).Title.StartsWith("selftest_menu"));

        test.Run(() => saved = Code());
        Pick("File", () => "Save");
        test.Wait();
        test.Check("save: the file is the code that is on screen", () =>
            System.IO.File.Exists(savedFile) && System.IO.File.ReadAllText(savedFile) == saved && document.Path == savedFile);

        Pick("File", () => "New layout");
        test.Wait();
        test.Check("tabs: a new layout has a tab of its own and is the one on screen", () =>
            documents.Count == 2 && docTabs.Tabs.Count == 2 && docTabs.Selected == 1 && !documents[0].Module.Enabled && documents[1].Module.Enabled && treeRows.Count == 0);

        test.Click(In(() => docTabs.TabBounds(0), 0.3f, 0.5f));
        test.Wait();
        test.Check("tabs: clicking a tab shows its layout", () => document == documents[0] && nameBox.Text == "selftest_menu" && treeRows.Count == 3);

        Pick("File", () => "Close");
        test.Wait();
        test.Check("close: the layout is gone and the other one is shown", () => documents.Count == 1 && document.Path is null && docTabs.Tabs.Count == 1);

        Pick("File", () => "Open...");
        test.Wait();
        test.Check("open: the list shows the way up and the layouts of the folder", () =>
            browsing && fileRows.Count >= 3 && fileRows[0].IsDirectory
            && fileRows.Exists(row => !row.IsDirectory && Path.GetFileName(row.Path) == "selftest_menu.hor")
            && fileRows.Exists(row => Path.GetFileName(row.Path) == "item.hor"));

        test.Click(FileRow("selftest_menu.hor"));
        test.Settle(0.3f);
        test.Check("open: the layout is back the way it was saved", () =>
            !browsing && documents.Count == 1 && document.Name == "selftest_menu" && Code() == saved && document.Walk().Count() == 3);
        test.Check("open: items and all", () => document.PreviewCount == 3);
        test.Check("open: opening it again shows the one that is open", () => Open(savedFile) && documents.Count == 1);

        test.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(testFolder, "sub"));
            System.IO.File.WriteAllText(Path.Combine(testFolder, "sub", "inner.hor"), "let inner = compositor.label({ text: \"inner\" });\n");
        });
        Pick("File", () => "Open...");
        test.Wait();
        test.Click(FileRow("sub"));
        test.Wait();
        test.Check("open: clicking a folder goes into it", () =>
            browsing && browseDirectory == Path.Combine(testFolder, "sub") && fileRows.Count == 2 && Path.GetFileName(fileRows[1].Path) == "inner.hor");
        test.Click(TreeRow(() => fileRows[0].Row));
        test.Wait();
        test.Check("open: and the first row goes back up", () => browseDirectory == testFolder);
        Pick("File", () => "Open...");
        test.Wait();
        test.Check("open: pressing open again goes back to the layout", () => !browsing && treeRows.Count == 3);
    }

    private void TestOwnLayout(HexPointerScript test)
    {
        /* The editor in itself */

        string own = System.IO.File.ReadAllText(EDITOR_LAYOUT);

        Pick("Help", () => "The editor's own UI");
        test.Settle(0.4f);
        test.Check("bootstrap: the editor opens its own UI", () => document.Name == "editor" && documents.Count == 2 && document.Walk().Count() == layout.Parts.Count);
        test.Check("bootstrap: and writes it back the same", () => CheckRoundTrip(document));
        test.Check("bootstrap: with stand-ins where it fills things in itself", () => document.PreviewCount >= 20);

        Pick("File", () => "Save");
        test.Wait();
        test.Check("bootstrap: saving it makes a copy rather than touch the file the editor runs on", () =>
            document.Path == Path.Combine(testFolder, "editor_edited.hor") && System.IO.File.Exists(document.Path) && System.IO.File.ReadAllText(EDITOR_LAYOUT) == own);
    }

    private void TestScrolling(HexPointerScript test)
    {
        /* Scrolling */

        test.Hover(Part("tree_scroll"));
        test.Run(() => compositor.Scroll(-3));
        test.Settle(0.2f);
        test.Check("scroll: the wheel scrolls the list under the pointer", () => Near(treeScroll.Offset, 3 * treeScroll.WheelStep));

        if (options.Wheel)
        {
            float offset = 0;
            test.Run(() =>
            {
                offset = treeScroll.Offset;
                Console.WriteLine("[Hex self-test] waiting for the mouse wheel");
            });
            test.WaitFor(() => treeScroll.Offset != offset, 10.0f);
            test.Check("scroll: the real mouse wheel gets there too", () => treeScroll.Offset != offset);
        }
    }

    private void TestEditorSize(HexPointerScript test)
    {
        /* The size of the editor */

        float scaleBefore = 0.0f, scaleAfter = 0.0f;
        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            scaleAfter = Scales[(Array.IndexOf(Scales, scaleBefore) + 1) % Scales.Length];
        });
        Pick("View", () => DescribeScale(scaleAfter));
        test.Settle(0.4f);
        test.Check("scale: the menu draws the editor and the layouts at the next size", () =>
            compositor.Scale == scaleAfter && compositor.UIScale == scaleAfter && stage.UIScale == scaleAfter);
        test.Check("scale: and the columns make do with the room that leaves", Fits);
        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("scale: buttons are still where they are drawn", () => layoutDebugger.IsOn);
        Pick("View", () => "Layout debugger");
        Pick("View", () => DescribeScale(scaleBefore));
        test.Settle(0.4f);
        test.Check("scale: and back", () => compositor.UIScale == scaleBefore && Fits());
    }

    private void TestLayoutDebugger(HexPointerScript test)
    {
        /* The layout debugger */

        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("debugger: the menu switches the layout debugger on", () => layoutDebugger.IsOn && stage.LayoutOverlay == layoutDebugger.Options);

        // Zoomed far in the layout is bigger than the canvas, and neither it nor the boxes the debugger draws
        // about it may run on out of the canvas over the lists either side
        test.Run(() => ZoomCanvas(8.0f, canvas.Bounds.Center));
        test.Settle(0.4f);
        test.Check("debugger: zoomed in, the layout and what is drawn about it are cut off at the canvas", () =>
            document.Module.Clip is { } cut && Near(cut.Width * document.Module.Scale.X, canvas.Bounds.Width, 1.0f)
            && Near(cut.Height * document.Module.Scale.Y, canvas.Bounds.Height, 1.0f) && document.Module.Frame.Width * document.Module.Scale.X > canvas.Bounds.Width);
        test.Run(ZoomToFit);
        test.Wait();
        Pick("View", () => "Layout debugger");
        test.Wait();
        test.Check("debugger: and off again", () => !layoutDebugger.IsOn && stage.LayoutOverlay is null);
    }
}
