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

        Pick("File", () => "New Layout");
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

    // A layout somebody wrote notes into, over the header, the design, a component, and one inside a component
    private const string NOTED_LAYOUT = "// noted_menu, laid out with Horizon.Hex.\n"
        + "// Load it with UILayout.Load, every component is there under the name it has here.\n"
        + "// The menu of the test, with notes in it that a save has to keep.\n// Two lines of them.\n#define layout\n\n"
        + "// The screen it was made for\ncompositor.design({ size: vec(1600, 900), fit: \"contain\" });\n\n"
        + "// The box everything sits in\nlet box = compositor.stack({\n    padding: vec(16, 16, 16, 16),\n    color: vec(0.1, 0.12, 0.17, 0.9)\n});\n\n"
        + "let go = compositor.button({\n    parent: box,\n    // one inside a declaration, which goes\n    label: \"Go { not a brace }\"\n});\n";

    private void TestNotes(HexPointerScript test)
    {
        HexDocument? noted = null;
        string written = string.Empty;

        test.Run(() =>
        {
            noted = new HexDocument("noted_menu", stage.CreateModule());
            noted.Module.Enabled = false;
            noted.Load(NOTED_LAYOUT);
            written = noted.GenerateCode().ReplaceLineEndings("\n");
        });
        test.Check("notes: the comments over the header and over each declaration are written back where they were", () =>
            written.Contains("// The menu of the test, with notes in it that a save has to keep.\n// Two lines of them.\n#define layout\n\n// The screen it was made for\ncompositor.design(")
            && written.Contains("\n\n// The box everything sits in\nlet box = compositor.stack(")
            && written.Contains("\n\nlet go = compositor.button(")
            && !written.Contains("one inside"));
        test.Check("notes: the two lines of the header are written once", () => written.Split("laid out with Horizon.Hex").Length == 2 && written.Split("Load it with UILayout.Load").Length == 2);

        test.Run(() =>
        {
            noted!.Find("box")!.Position += new Vector2(10, 0);
            noted.Rename(noted.Find("go")!, "start");
            written = noted.GenerateCode().ReplaceLineEndings("\n");
        });
        test.Check("notes: moved and renamed, the notes are still over what they were over", () =>
            written.Contains("// The box everything sits in\nlet box = compositor.stack(") && written.Contains("let start = compositor.button(") && CheckRoundTrip(noted!));
        test.Run(() => stage.RemoveModule(noted!.Module));
    }

    private void TestOwnLayout(HexPointerScript test)
    {
        /* The editor in itself */

        string own = System.IO.File.ReadAllText(EDITOR_LAYOUT);

        Pick("Help", () => "Open Hex's Own Layout");
        test.Settle(0.4f);
        test.Check("bootstrap: the editor opens its own UI, its parts in files of their own included", () =>
            document.Name == "editor" && documents.Count == 2 && document.Walk().Count() == layout.Parts.Count(pair => !HexDocument.InsideInclude(pair.Value))
            && document.Walk().Count(pair => pair.Component is Include) == 5);
        test.Check("bootstrap: and writes it back the same", () => CheckRoundTrip(document));
        test.Check("bootstrap: with stand-ins where it fills things in itself, inside the included files too", () =>
            document.PreviewCount >= 5 && document.Find("side_bar") is Include side && Descendants(side).OfType<GridPanel>().Any(grid => grid.Children.Count == 19));

        // The parts that take turns are on layers, all but the designer's hidden as it opens, and running Hex shows them all
        string[] startHidden = ["files", "code", "table", "extension", "values"];
        test.Check("layers: the editor's views are on layers, the ones that take turns with the designer's hidden", () =>
            document.Module.Layers.Count == 8 && document.Module.HiddenLayers.Count == startHidden.Length
            && startHidden.All(document.Module.IsLayerHidden) && chrome.HiddenLayers.Count == 0);

        // The list of layers is short, the row scrolled to first the way somebody would
        test.Run(() => layersScroll.ScrollIntoView(layerRows["code"]));
        test.Wait();
        test.Click(At(() => layerRows.GetValueOrDefault("code")));
        test.Wait();
        test.Check("layers: a click on one shows it to work on, and the code says the layout starts with the rest hidden", () =>
            !document.Module.IsLayerHidden("code") && Code().Contains("compositor.hidden_layers([\"files\", \"table\", \"extension\", \"values\"]);"));

        test.Run(() => layersScroll.ScrollIntoView(layerRows["code"]));
        test.Wait();
        test.Click(At(() => layerRows.GetValueOrDefault("code")));
        test.Wait();
        test.Check("layers: and another hides it again", () => document.Module.IsLayerHidden("code") && Code().Contains("\"code\""));

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
        // Eased there a few pixels a tick (UIScrollEase), not jumped
        test.WaitFor(() => Near(treeScroll.Offset, 3 * treeScroll.WheelStep), 2.0f);
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
        Pick("View", () => "Layout Debugger");
        test.Wait();
        test.Check("scale: buttons are still where they are drawn", () => layoutDebugger.IsOn);
        Pick("View", () => "Layout Debugger");
        Pick("View", () => DescribeScale(scaleBefore));
        test.Settle(0.4f);
        test.Check("scale: and back", () => compositor.UIScale == scaleBefore && Fits());
    }

    private void TestLayoutDebugger(HexPointerScript test)
    {
        /* The layout debugger */

        Pick("View", () => "Layout Debugger");
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
        Pick("View", () => "Layout Debugger");
        test.Wait();
        test.Check("debugger: and off again", () => !layoutDebugger.IsOn && stage.LayoutOverlay is null);
    }
}
