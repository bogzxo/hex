using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>What a row of the side tree does, pressed, opened, filled in as it opens, its right click and the keys it takes.</summary>
internal sealed record SideTag(Action? Pressed = null, Action<Vector2>? Menu = null, Action<UITreeItem>? Toggled = null, Action<UITreeItem>? Expanding = null, Func<char, bool>? Key = null);

internal readonly record struct ExplorerEntry(string Path, bool IsFolder, int Depth, bool IsRoot);

// The side bar's lists for code, the explorer, the outline, what was found and the files of a folder, all one tree the
// side bar paints itself (a TreeView). They were a row a component made out of a template, every one of them made again
// for any change, a selection that moved included. The designer's components and layers are still rows of their own.
internal sealed partial class DawnScene
{
    private TreeView sideTree = null!;

    // What the folders of the workspace have in them, read once and watched
    private readonly HexFolders folders = new();

    // The outline's rows that were closed, by their name and line, so they stay closed while the file is typed into
    private readonly HashSet<string> outlineClosed = new(StringComparer.Ordinal);

    private void BuildSideTree()
    {
        // Every walk of the workspace's files goes by the settings' files_exclude, search_exclude and .gitignores, read
        // as each one starts, so a settings file read again or a folder's own settings count straight away
        HexSearch.Excluded = () => settings is { } kept ? HexExcludes.Of(kept) : HexExcludes.Usual;

        sideTree = layout.Get<TreeView>("side_tree");
        sideTree.Visible = false;
        sideTree.OnPressed = item => (item.Tag as SideTag)?.Pressed?.Invoke();
        sideTree.OnActivated = item => (item.Tag as SideTag)?.Pressed?.Invoke();
        sideTree.OnToggled = item => (item.Tag as SideTag)?.Toggled?.Invoke(item);
        sideTree.OnExpanding = item => (item.Tag as SideTag)?.Expanding?.Invoke(item);
        sideTree.OnItemContextMenu = (item, point) => (item.Tag as SideTag)?.Menu?.Invoke(point);
        sideTree.OnKey = (item, key) => (item.Tag as SideTag)?.Key?.Invoke(key) == true;

        // As wide as the components' list whichever of the two is showing, dragged by its edge the same way
        SideGrip(sideTree, UIResize.Right, left: true);
    }

    /// <summary>Whether the side bar shows the tree, or the rows of the designer.</summary>
    private void UseSideTree(bool tree)
    {
        sideTree.Visible = tree;
        treeScroll.Visible = !tree;
    }

    private static UITreeItem NoteItem(string text, Vector4 color) => HexNotes.Note(text, color);

    /* The explorer */

    /// <summary>The folders of the workspace as a tree, a folder's files read when it is opened and kept until they change.</summary>
    private void RebuildExplorer()
    {
        treeTitle.Text = Said("side.explorer_of", ("workspace", explorer.workspace.Name));
        explorer.explorerRows.Clear();
        explorerMade = 0;
        UseSideTree(true);
        WatchFolders();

        var items = new List<UITreeItem>();
        foreach (string folder in explorer.workspace.Folders)
            items.Add(FolderItem(new ExplorerEntry(folder, true, 0, true)));

        if (items.Count == 0)
            items.Add(NoteItem(Said("side_tree.no_folders"), DimColor));

        sideTree.Items = items;
        DecorateRows();
        ShowOpenInExplorer();
    }

    // How many rows of the explorer were made, there is a most
    private int explorerMade;

    private UITreeItem FolderItem(ExplorerEntry entry)
    {
        bool open = explorer.workspace.Expanded.Contains(entry.Path);
        var item = new UITreeItem(Path.GetFileName(entry.Path.TrimEnd('\\', '/')), open ? "folder-opened" : "folder")
        {
            IconColor = FolderColor,
            Color = entry.IsRoot ? null : FolderColor,
            Tooltip = entry.Path,
            Expandable = true,
            Expanded = open
        };

        item.Tag = new SideTag(
            Menu: _ => explorer.ShowExplorerMenu(entry),
            Toggled: toggled =>
            {
                toggled.Icon = toggled.Expanded ? "folder-opened" : "folder";
                if (toggled.Expanded) explorer.workspace.Expanded.Add(entry.Path);
                else explorer.workspace.Expanded.Remove(entry.Path);
                explorer.KeepWorkspace();
            },
            Expanding: FillFolder);

        explorer.explorerRows.Add((entry.Path, true, item));
        explorerMade++;
        DecorateRow(item, entry.Path, true, entry.IsRoot ? null : FolderColor);

        if (open)
            FillFolder(item);

        return item;
    }

    /// <summary>Helper method to put the rows of what is in a folder under it, as it opens.</summary>
    private void FillFolder(UITreeItem item)
    {
        if (item.Children is not null || item.Tooltip is not { Length: > 0 } folder)
            return;

        item.Children = [];
        var (inside, files) = folders.In(folder);
        int depth = 1;
        UITreeItem root = item;
        for (UITreeItem? at = item.Parent; at is not null; at = at.Parent)
        {
            depth++;
            root = at;
        }

        // Only what files.exclude says is gone from the explorer, .github, .env, bin and node_modules all show the way
        // they do in VS Code. Search.exclude and the .gitignores are quick open's and Find in Files' business
        HexExcludes excludes = HexExcludes.Of(settings);
        string top = root.Tooltip ?? folder;
        bool Hidden(string path, bool isFolder) => excludes.Hidden(HexExcludes.Relative(top, path), isFolder);

        foreach (string directory in inside)
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith("~syncthing~") || explorerMade >= HexExplorer.MAX_EXPLORER_ROWS || Hidden(directory, true))
                continue;

            item.Add(FolderItem(new ExplorerEntry(directory, true, depth, false)));
        }

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("~syncthing~") || name.Contains(".sync-conflict") || explorerMade >= HexExplorer.MAX_EXPLORER_ROWS || Hidden(file, false))
                continue;

            var entry = new ExplorerEntry(file, false, depth, false);
            var row = item.Add(new UITreeItem(name, HexExplorer.FileIcon(file))
            {
                IconColor = explorer.FileIconColor(file),
                Tooltip = file,
                Tag = new SideTag(Pressed: () => explorer.ExplorerPressed(entry), Menu: _ => explorer.ShowExplorerMenu(entry))
            });

            explorer.explorerRows.Add((file, false, row));
            explorerMade++;
            DecorateRow(row, file, false, null);
        }

        // A folder of the workspace with nothing in it says so, with the two ways of putting something in it
        if (item.Children.Count == 0 && explorer.workspace.Folders.Contains(folder, PlatformPaths.Comparer))
            AddEmptyFolderNote(item, folder);
    }

    /// <summary>The file that is open selected in the explorer, if the explorer has it.</summary>
    private void ShowOpenInExplorer()
    {
        if (document.Path is not { } path)
            return;

        string full = Path.GetFullPath(path);
        foreach (var (rowPath, isFolder, row) in explorer.explorerRows)
        {
            if (!isFolder && string.Equals(Path.GetFullPath(rowPath), full, PlatformPaths.Comparison))
            {
                sideTree.Selected = row;
                return;
            }
        }
    }

    /* The files of the folder layouts are opened from, without a workspace */

    private void RebuildFiles()
    {
        // A workspace is a tree of its folders, without one it is the folder files are opened from
        if (!explorer.workspace.IsEmpty)
        {
            RebuildExplorer();
            return;
        }

        treeTitle.Text = Said("side.files_of", ("folder", Path.GetFileName(browseDirectory.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : browseDirectory));
        UseSideTree(true);
        folders.Forget();

        // A folder to open first, a row that does it the way VS Code's button does, then the folder files are opened from.
        // It was a quiet note saying which menu to go to, over a blue "../"
        var items = new List<UITreeItem>
        {
            new(Said("side_tree.open_folder"), "folder-opened")
            {
                Color = compositor.Skin?.Palette.Highlight,
                IconColor = compositor.Skin?.Palette.Highlight,
                Tag = new SideTag(Pressed: () => explorer.AskFolder(explorer.OpenFolder))
            }
        };
        if (Directory.GetParent(browseDirectory) is { } parent)
            items.Add(FileItem(parent.FullName, true, "..", "arrow-up"));

        var (inside, files) = folders.In(browseDirectory);
        foreach (string directory in inside)
            items.Add(FileItem(directory, true, Path.GetFileName(directory)));

        // Every file, the editor opens any of them, and asks first about one that isn't text
        foreach (string file in files)
            items.Add(FileItem(file, false, Path.GetFileName(file)));

        sideTree.Items = items;
        DecorateRows();
    }

    private UITreeItem FileItem(string path, bool isDirectory, string text, string? icon = null)
    {
        var item = new UITreeItem(text, icon ?? (isDirectory ? "folder" : HexExplorer.FileIcon(path)))
        {
            IconColor = icon is not null ? DimColor : isDirectory ? FolderColor : explorer.FileIconColor(path),
            Color = icon is not null ? DimColor : null,
            Tooltip = path,
            Tag = new SideTag(Pressed: () => explorer.PickFile(path, isDirectory))
        };

        fileRows.Add((path, isDirectory, item));
        return item;
    }

    /* The outline of a file of code */

    private void RebuildOutline()
    {
        UseSideTree(true);

        // Under the plain welcome there's no file, the empty layout it's over is nobody's and has nothing to outline
        if (plainShown)
        {
            treeTitle.Text = Said("side.outline");
            sideTree.Items = [NoteItem(Said("side_tree.outline_no_file"), DimColor)];
            return;
        }

        treeTitle.Text = Said("side.outline_of", ("file", document.Name));

        List<OutlineEntry> entries = KeptOutline();
        var items = new List<UITreeItem>();
        var open = new List<(int Depth, UITreeItem Item, string Key)>();

        foreach (OutlineEntry entry in entries)
        {
            // Inside of the last one that is less deep
            while (open.Count > 0 && open[^1].Depth >= entry.Depth)
                open.RemoveAt(open.Count - 1);

            string key = $"{entry.Name}@{entry.Line}";
            var item = new UITreeItem(entry.Name, entry.Icon)
            {
                IconColor = OutlineColor(entry.Icon),
                Tooltip = Said("side_tree.outline_line", ("line", (entry.Line + 1).ToString())),
                Expanded = !outlineClosed.Contains(key),
                Tag = new SideTag(
                    Pressed: () =>
                    {
                        code.GoTo(entry.Line, entry.Column);
                        code.Focus();
                    },
                    Toggled: toggled =>
                    {
                        if (toggled.Expanded) outlineClosed.Remove(key);
                        else outlineClosed.Add(key);
                    })
            };

            if (open.Count > 0)
                open[^1].Item.Add(item);
            else
                items.Add(item);

            open.Add((entry.Depth, item, key));
        }

        if (items.Count == 0)
            items.Add(NoteItem(Said("side_tree.outline_empty"), DimColor));

        // Opening a row only shows what is in it, it doesn't go there, the way an outline does
        sideTree.Items = items;
        designer.RebuildLayers();
    }

    /// <summary>The colour of an icon of the outline, the same as the values give the same kind of thing.</summary>
    private static Vector4? OutlineColor(string icon) => icon switch
    {
        "symbol-object" => HexRunning.SymbolObject,
        "symbol-array" or "symbol-method" => HexRunning.SymbolList,
        "symbol-string" => HexRunning.SymbolText,
        "symbol-number" => HexRunning.SymbolNumber,
        "symbol-boolean" or "symbol-null" => HexRunning.SymbolConstant,
        "symbol-color" => HexRunning.SymbolColour,
        _ => HexRunning.SymbolField
    };

    /// <summary>
    /// What the files of the workspace leave out changed (files_exclude, search_exclude, the .gitignores). The explorer
    /// is put together again with what's hidden now, quick open and Find in Files read it the next time they walk.
    /// </summary>
    private void ExcludesChanged() => treeDirty |= Side == HexSide.Explorer;

    /// <summary>The list in the side bar, whatever its view is.</summary>
    private void RebuildTree()
    {
        designer.treeRows.Clear();
        fileRows.Clear();
        ApplySide();
        ShowExplorerHead(Side);
        PlaceSearchView(Side == HexSide.Search);
        PlaceCommitBox(Side == HexSide.SourceControl);

        switch (Side)
        {
            case HexSide.Explorer:
                RebuildFiles();
                break;
            case HexSide.Search:
                RebuildResults();
                break;
            case HexSide.Design:
                designer.RebuildComponentTree();
                break;
            default:
                if (!RebuildSideView())
                    RebuildOutline();
                break;
        }
    }
}
