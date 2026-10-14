using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The lists the editor fills in. The tabs, the tree of the layout, its layers, and the files of a folder when a layout is being opened.
internal sealed partial class HexScene
{
    /// <summary>The list in the side bar, whatever its view is.</summary>
    private void RebuildTree()
    {
        treeRows.Clear();
        fileRows.Clear();
        ApplySide();

        switch (Side)
        {
            case HexSide.Explorer:
                RebuildFiles();
                break;
            case HexSide.Search:
                RebuildResults();
                break;
            case HexSide.Design:
                RebuildComponentTree();
                break;
            default:
                RebuildOutline();
                break;
        }
    }

    private void RebuildComponentTree()
    {
        treeTitle.Text = Said("side.designer");

        var components = document.Walk().ToList();
        componentCount = components.Count;
        var items = layout.Populate(tree, components.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var (component, depth) = components[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            // The text steps in with how deep in the layout the component is
            string kind = UIModule.KindOf(component);
            caption.Text = $"$({KindIcon(kind)}) {document.NameOf(component)}  [color:#7f8a9e]{kind}[/color]";
            caption.Position = new Vector2(10 + depth * 14, 0);
            caption.Color = component.Visible && !component.IsHiddenByLayer ? null : DimColor;

            // Ctrl or shift adds a row to what's selected, a right click is the menu for it (and the rest of what's selected)
            row.Selected = document.IsSelected(component);
            row.OnPressed = () => Pick(component);
            row.OnContextMenu = _ =>
            {
                if (!document.IsSelected(component))
                    Select(component);
                ShowComponentMenu(compositor.Pointer.Position);
            };
            treeRows[component] = row;
        }

        if (components.Count == 0)
            Hint(tree, "nothing yet, add something\nout of the toolbox above");

        RebuildLayers();
    }

    /* Layers: parts of a layout that are shown and hidden together, so what is laid over something else can be put
       out of the way while that is worked on. Which layer a component is on is one of its properties and goes into
       the file, which of them are hidden right now is only how the editor is looking at the layout. */

    private void RebuildLayers()
    {
        layerRows.Clear();

        var names = document.Module.Layers;
        var items = layout.Populate(layers, names.Count);

        for (int i = 0; i < items.Count; i++)
        {
            string name = names[i];
            bool hidden = document.Module.IsLayerHidden(name);

            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = hidden ? $"$(eye-closed) {Plain(name)}  (hidden)" : $"$(eye) {Plain(name)}";
            caption.Color = hidden ? DimColor : null;

            row.OnPressed = () => ToggleLayer(name);
            layerRows[name] = row;
        }

        if (names.Count == 0)
        {
            Hint(layers, "none yet, a component gets one\nin its properties, under layer");
        }
    }

    private void ToggleLayer(string name)
    {
        bool show = document.Module.IsLayerHidden(name);
        document.Module.SetLayerVisible(name, show);

        // The tree greys out what is out of sight
        treeDirty = true;
        Say(show ? $"showing the layer {name}" : $"hid the layer {name}, it is still in the layout and in the file");
    }

    private void ShowAllLayers()
    {
        document.Module.ShowAllLayers();
        treeDirty = true;
    }

    /// <summary>
    /// Helper to list the folder a layout is being opened from. The way up, the folders in it and its layouts.
    /// </summary>
    private void RebuildFiles()
    {
        // A workspace is a tree of its folders, without one it is the folder files are opened from
        if (!workspace.IsEmpty)
        {
            RebuildExplorer();
            return;
        }

        treeTitle.Text = Said("side.open_a_layout");
        treeScroll.Offset = 0;

        var entries = new List<(string Path, bool IsDirectory, string Text)>();
        try
        {
            if (Directory.GetParent(browseDirectory) is { } parent)
                entries.Add((parent.FullName, true, "../"));

            foreach (string directory in Directory.GetDirectories(browseDirectory).Order())
                entries.Add((directory, true, Path.GetFileName(directory) + "/"));

            foreach (string file in Directory.GetFiles(browseDirectory, "*.hor").Order())
                entries.Add((file, false, Path.GetFileName(file)));
        }
        catch (Exception e)
        {
            Say($"couldn't look into {browseDirectory}: {e.Message}", error: true);
        }

        var items = layout.Populate(tree, entries.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var (path, isDirectory, text) = entries[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = isDirectory ? $"$(folder) {Plain(text)}" : $"$({FileIcon(path)}) {Plain(text)}";
            caption.Color = isDirectory ? FolderColor : null;
            row.OnPressed = () => PickFile(path, isDirectory);
            fileRows.Add((path, isDirectory, row));
        }
    }

    /// <summary>The icon of a file in the explorer and the lists, by what it is.</summary>
    private static string FileIcon(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == HexWorkspace.EXTENSION)
            return "folder-library";
        if (name.Equals("skin.hor", StringComparison.OrdinalIgnoreCase))
            return "paintcan";
        if (extension == ".hor")
            return Path.GetFileName(Path.GetDirectoryName(path))?.Equals("localisation", StringComparison.OrdinalIgnoreCase) == true ? "globe" : "file-code";

        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" => "file-media",
            ".ttf" or ".otf" or ".fnt" => "symbol-text",
            ".md" or ".txt" => "book",
            ".slang" or ".hlsl" or ".cs" or ".py" or ".lua" => "code",
            ".json" or ".tmx" or ".tsx" or ".xml" => "json",
            _ => "file"
        };
    }

    private void PickFile(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = path;
            treeDirty = true;
            Say($"files of {browseDirectory}");
            return;
        }

        Open(path);
    }
}
