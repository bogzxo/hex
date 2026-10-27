using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of the shell round the editor. The side bar one width whatever view is in it and nothing pushed past the
// window's right edge, every view's empty note the one way, the title over the welcome, a long name's tab cut short,
// Extensions lit while its window is up, the explorer's own buttons and its right click on nothing, and the status bar
// and the title bar kept off their edges.
internal sealed partial class DawnScene
{
    private void TestShellLayout(HexPointerScript test)
    {
        string root = string.Empty, empty = string.Empty, longName = string.Empty;
        var widths = new Dictionary<HexSide, (float Side, float Right)>();
        StackPanel body = layout.Get<StackPanel>("body");

        test.Run(() =>
        {
            root = Path.Combine(testFolder, "shell");
            empty = Path.Combine(root, "empty");
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            Directory.CreateDirectory(empty);
            longName = Path.Combine(root, "a_really_very_long_file_name_that_should_be_cut_short_in_its_tab_somewhere.txt");
            File.WriteAllText(longName, "long\n");
            explorer.AddFolder(root);
            left.Show(true);
        });
        test.Wait();

        // Every view, the side bar the width it was dragged to and the middle in what the window has left
        foreach (HexSide side in (HexSide[])[HexSide.Explorer, HexSide.Search, HexSide.Outline, HexSide.SourceControl, HexSide.Debug])
        {
            test.Run(() => ShowSide(side));
            test.Settle(0.4f);
            test.Run(() => widths[side] = (left.Bounds.Width, body.Bounds.Max.X));
        }

        test.Check("shell: the side bar is the one width whatever view is up, search's boxes included", () =>
            widths.Count == 5 && widths.Values.All(each => MathF.Abs(each.Side - (treeScroll.Size.X + left.Padding.Total.X)) < 1.5f));
        test.Check("shell: and nothing in the window goes past its right edge", () =>
            widths.Values.All(each => each.Right <= chrome.Root.Bounds.Max.X + 0.5f));

        // Search's hint is the tree's note, an icon and the text broken into rows at the side's width, nothing under the boxes
        test.Run(() =>
        {
            ShowSide(HexSide.Search);
            searchQuery.Text = string.Empty;
            SearchNow();
            RebuildTree();
        });
        test.Settle(0.3f);
        test.Check("shell: search's hint before anything's looked for is a note of the tree that goes onto more rows, the line under the boxes hidden", () =>
            sideTree.Items is [{ Wraps: true, Icon: HexNotes.ICON } note] && note.Text == Said("search.hint") && sideTree.VisibleCount > 1
            && !searchMessage.Visible);

        // An empty folder of the workspace says so, with New File and New Folder under it
        test.Run(() =>
        {
            explorer.AddFolder(empty);
            ShowSide(HexSide.Explorer);
            RebuildTree();
        });
        test.Settle(0.3f);
        test.Check("shell: an empty folder of the workspace has a note and New File and New Folder under its row", () =>
            sideTree.Items.FirstOrDefault(item => item.Tooltip == empty) is { Children: [{ Wraps: true }, { } file, { } folder] }
            && file.Text == Said("side_tree.new_file") && folder.Text == Said("side_tree.new_folder"));
        test.Check("shell: the explorer's title has New File, New Folder, Collapse Folders and Refresh", () =>
            newFileButton.Visible && newFolderButton.Visible && collapseButton.Visible && refreshButton.Visible);
        test.Run(() => sideTree.OnEmptyContextMenu?.Invoke(sideTree.Bounds.Center));
        test.Wait();
        test.Check("shell: a right click on nothing in the explorer has New File, New Folder, Refresh and Collapse Folders", () =>
            contextMenu?.Showing is { } open && new[] { "explorer_files.new_file", "explorer_files.new_folder", "side.refresh", "side.collapse" }.All(key => open.Items.Any(item => item.Label == Said(key))));
        test.Run(() => contextMenu?.Close());

        // A long name's tab is cut short in the middle, its end still there
        test.Run(() => Open(longName));
        test.Settle(0.3f);
        test.Check("shell: a tab of a long name is no wider than the most a tab is", () =>
            docTabs.Tabs.FindIndex(tab => tab.Tag == document) is >= 0 and var at && docTabs.TabBounds(at).Width <= docTabs.MaxTabWidth + 0.5f && docTabs.MaxTabWidth > 0.0f);

        // The welcome over the empty layout says it's the welcome, not the layout under it, and the outline has nothing
        string? title = null, outlineTitle = null;
        UITreeItem? outlineNote = null;
        test.Run(() =>
        {
            plainShown = true;
            RetitleWindow();
            title = ((TitleBar)top).Title;
            ShowSide(HexSide.Outline);
            RebuildTree();
            outlineTitle = treeTitle.Text;
            outlineNote = sideTree.Items.FirstOrDefault();
            plainShown = false;
            RetitleWindow();
            treeDirty = true;
        });
        test.Check("shell: over the welcome the title says Welcome and the outline says there's no file", () =>
            title is { } said && said.StartsWith(Said("tabs.welcome"), StringComparison.Ordinal) && outlineTitle == Said("side.outline")
            && outlineNote is { Wraps: true } && outlineNote.Text == Said("side_tree.outline_no_file"));

        // Extensions lit while its window's up and the side's own button not, and let go of when it closes
        test.Run(() => ShowSide(HexSide.Debug));
        test.Wait();
        test.Run(ShowExtensions);
        test.Wait();
        test.Check("shell: Extensions is lit while its window is up, the side's button isn't", () =>
            layout.Get<Button>("act_extensions").Selected && !layout.Get<Button>("act_debug").Selected && SettingsWindow is not null);
        test.Run(CloseSettings);
        test.Wait();
        test.Check("shell: and once it's closed the side's button is lit again", () =>
            !layout.Get<Button>("act_extensions").Selected && layout.Get<Button>("act_debug").Selected);

        // The status bar's first item and the Dusk button kept off what's next to them
        test.Check("shell: the status bar's items are kept off its ends, and the title bar has room between the menus and Dusk", () =>
            statusBar.Items.Where(item => !item.Right).Select(item => statusBar.ItemBounds(item)).FirstOrDefault(bounds => !bounds.IsEmpty) is { } first
            && first.Min.X >= statusBar.Bounds.Min.X + 4.0f && modeToggle.Bounds.Min.X - menu.Bounds.Max.X >= 4.0f);

        test.Run(() =>
        {
            CloseNow();
            explorer.RemoveFolder(empty);
            explorer.RemoveFolder(root);
            ShowSide(HexSide.Explorer);
        });
        test.Wait();
    }
}
