using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The side bar's lists for code, the explorer, the outline, what was found and the files of a folder, all one tree the
// side bar paints itself (a TreeView). They were a row a component made out of a template, every one of them made again
// for any change, a selection that moved included. The designer's components and layers are still rows of their own.
internal sealed partial class HexScene
{
    private TreeView sideTree = null!;

    // What the folders of the workspace have in them, read once and watched
    private readonly HexFolders folders = new();

    // The outline's rows that were closed, by their name and line, so they stay closed while the file is typed into
    private readonly HashSet<string> outlineClosed = new(StringComparer.Ordinal);

    /// <summary>What a row of the side tree does, pressed, opened, filled in as it opens, and its right click.</summary>
    private sealed record SideTag(Action? Pressed = null, Action<Vector2>? Menu = null, Action<UITreeItem>? Toggled = null, Action<UITreeItem>? Expanding = null);

    private void BuildSideTree()
    {
        sideTree = layout.Get<TreeView>("side_tree");
        sideTree.Visible = false;
        sideTree.FocusLine = true;
        sideTree.OnPressed = item => (item.Tag as SideTag)?.Pressed?.Invoke();
        sideTree.OnActivated = item => (item.Tag as SideTag)?.Pressed?.Invoke();
        sideTree.OnToggled = item => (item.Tag as SideTag)?.Toggled?.Invoke(item);
        sideTree.OnExpanding = item => (item.Tag as SideTag)?.Expanding?.Invoke(item);
        sideTree.OnItemContextMenu = (item, point) => (item.Tag as SideTag)?.Menu?.Invoke(point);

        // As wide as the components' list whichever of the two is showing, dragged by its edge the same way
        SideGrip(sideTree, UIResize.Right, left: true);
    }

    /// <summary>Whether the side bar shows the tree, or the rows of the designer (and the search's empty page).</summary>
    private void UseSideTree(bool tree)
    {
        sideTree.Visible = tree;
        treeScroll.Visible = !tree;
    }

    private static UITreeItem NoteItem(string text, Vector4 color) => new(text) { Color = color };

    /* The explorer */

    private readonly record struct ExplorerEntry(string Path, bool IsFolder, int Depth, bool IsRoot);

    /// <summary>The folders of the workspace as a tree, a folder's files read when it is opened and kept until they change.</summary>
    private void RebuildExplorer()
    {
        treeTitle.Text = Said("side.explorer_of", ("workspace", workspace.Name.ToUpperInvariant()));
        explorerRows.Clear();
        explorerMade = 0;
        UseSideTree(true);
        folders.Watch(workspace.Folders);

        var items = new List<UITreeItem>();
        foreach (string folder in workspace.Folders)
            items.Add(FolderItem(new ExplorerEntry(folder, true, 0, true)));

        if (items.Count == 0)
            items.Add(NoteItem("no folders, File has Add folder", DimColor));

        sideTree.Items = items;
        ShowOpenInExplorer();
    }

    // How many rows of the explorer were made, there is a most
    private int explorerMade;

    private UITreeItem FolderItem(ExplorerEntry entry)
    {
        bool open = workspace.Expanded.Contains(entry.Path);
        var item = new UITreeItem(Path.GetFileName(entry.Path.TrimEnd('\\', '/')), open ? "folder-opened" : "folder")
        {
            IconColor = FolderColor,
            Color = entry.IsRoot ? null : FolderColor,
            Tooltip = entry.Path,
            Expandable = true,
            Expanded = open
        };

        item.Tag = new SideTag(
            Menu: _ => ShowExplorerMenu(entry),
            Toggled: toggled =>
            {
                toggled.Icon = toggled.Expanded ? "folder-opened" : "folder";
                if (toggled.Expanded) workspace.Expanded.Add(entry.Path);
                else workspace.Expanded.Remove(entry.Path);
                KeepWorkspace();
            },
            Expanding: FillFolder);

        explorerRows.Add((entry.Path, true, item));
        explorerMade++;

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
        for (UITreeItem? at = item.Parent; at is not null; at = at.Parent) depth++;

        foreach (string directory in inside)
        {
            string name = Path.GetFileName(directory);
            if (SkippedFolders.Contains(name) || name.StartsWith('.') || name.StartsWith("~syncthing~") || explorerMade >= MAX_EXPLORER_ROWS)
                continue;

            item.Add(FolderItem(new ExplorerEntry(directory, true, depth, false)));
        }

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("~syncthing~") || name.StartsWith('.') || name.Contains(".sync-conflict") || explorerMade >= MAX_EXPLORER_ROWS)
                continue;

            var entry = new ExplorerEntry(file, false, depth, false);
            var row = item.Add(new UITreeItem(name, FileIcon(file))
            {
                Color = IsEditable(file) ? null : DimColor,
                Tooltip = file,
                Tag = new SideTag(Pressed: () => ExplorerPressed(entry), Menu: _ => ShowExplorerMenu(entry))
            });

            explorerRows.Add((file, false, row));
            explorerMade++;
        }
    }

    /// <summary>The file that is open selected in the explorer, if the explorer has it.</summary>
    private void ShowOpenInExplorer()
    {
        if (document.Path is not { } path)
            return;

        string full = Path.GetFullPath(path);
        foreach (var (rowPath, isFolder, row) in explorerRows)
        {
            if (!isFolder && string.Equals(Path.GetFullPath(rowPath), full, StringComparison.OrdinalIgnoreCase))
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
        if (!workspace.IsEmpty)
        {
            RebuildExplorer();
            return;
        }

        treeTitle.Text = Said("side.open_a_layout");
        UseSideTree(true);
        folders.Forget();

        var items = new List<UITreeItem>();
        if (Directory.GetParent(browseDirectory) is { } parent)
            items.Add(FileItem(parent.FullName, true, "../"));

        var (inside, files) = folders.In(browseDirectory);
        foreach (string directory in inside)
            items.Add(FileItem(directory, true, Path.GetFileName(directory) + "/"));

        foreach (string file in files)
        {
            if (Path.GetExtension(file).Equals(".hor", StringComparison.OrdinalIgnoreCase))
                items.Add(FileItem(file, false, Path.GetFileName(file)));
        }

        sideTree.Items = items;
    }

    private UITreeItem FileItem(string path, bool isDirectory, string text)
    {
        var item = new UITreeItem(text, isDirectory ? "folder" : FileIcon(path))
        {
            IconColor = isDirectory ? FolderColor : null,
            Color = isDirectory ? FolderColor : null,
            Tooltip = path,
            Tag = new SideTag(Pressed: () => PickFile(path, isDirectory))
        };

        fileRows.Add((path, isDirectory, item));
        return item;
    }

    /* The outline of a file of code */

    private void RebuildOutline()
    {
        treeTitle.Text = Said("side.outline_of", ("file", document.Name.ToUpperInvariant()));
        UseSideTree(true);

        List<OutlineEntry> entries = Outline(code.Text);
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
                Tooltip = $"line {entry.Line + 1}",
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
            items.Add(NoteItem("nothing declared in here", DimColor));

        // Opening a row only shows what is in it, it doesn't go there, the way an outline does
        sideTree.Items = items;
        RebuildLayers();
    }

    /// <summary>The colour of an icon of the outline, the same as the values give the same kind of thing.</summary>
    private static Vector4? OutlineColor(string icon) => icon switch
    {
        "symbol-object" => SymbolObject,
        "symbol-array" or "symbol-method" => SymbolList,
        "symbol-string" => SymbolText,
        "symbol-number" => SymbolNumber,
        "symbol-boolean" or "symbol-null" => SymbolConstant,
        "symbol-color" => SymbolColour,
        _ => SymbolField
    };

    /* What was found, Ctrl+Shift+F and Shift+F12, by the file it is in */

    private void RebuildResults()
    {
        // Nothing looked for yet, the way to look
        if (results is null)
        {
            treeTitle.Text = Said("side.search");
            UseSideTree(false);
            layout.Populate(tree, 0);
            layout.Instantiate(SEARCH_EMPTY_TEMPLATE, tree).Get<Button>("find").OnPressed = FindInFiles;
            return;
        }

        treeTitle.Text = resultsTitle;
        UseSideTree(true);

        var items = new List<UITreeItem>();
        var byFile = new Dictionary<string, UITreeItem>(StringComparer.OrdinalIgnoreCase);
        foreach (HexResult result in results)
        {
            if (!byFile.TryGetValue(result.Path, out UITreeItem? file))
            {
                file = new UITreeItem(Path.GetFileName(result.Path), FileIcon(result.Path))
                {
                    Tooltip = result.Path,
                    Expanded = true,
                    Children = []
                };
                byFile[result.Path] = file;
                items.Add(file);
            }

            HexResult found = result;
            file.Add(new UITreeItem(result.Text.Trim(), string.Empty)
            {
                Detail = $"{result.Line + 1}",
                Tooltip = $"{result.Path}:{result.Line + 1}",
                Tag = new SideTag(Pressed: () => OpenAt(found.Path, found.Line, found.Column))
            });
        }

        foreach (UITreeItem file in items)
            file.Detail = file.Children!.Count.ToString();

        if (items.Count == 0)
            items.Add(NoteItem("nothing", DimColor));

        sideTree.Items = items;
    }
}
