using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

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
            case HexSide.Debug:
                RebuildDebugSide();
                break;
            default:
                RebuildOutline();
                break;
        }
    }

    private void RebuildComponentTree()
    {
        UseSideTree(false);
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
            string kind = UIModule.KindOf(component) ?? "component";
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
       the file, and so do the ones that are hidden, as the layers the layout starts with hidden (compositor.hidden_layers),
       the screens of a menu that aren't the first, the views of Hex that take turns. */

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

        // The tree greys out what is out of sight, and the code says which layers the layout starts with hidden
        treeDirty = codeDirty = true;
        Say(show ? $"showing the layer {name}" : $"hid the layer {name}, the layout starts with it hidden, everything on it is still in the file");
    }

    private void ShowAllLayers()
    {
        document.Module.ShowAllLayers();
        treeDirty = codeDirty = true;
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
            ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" => "file-media",
            ".ttf" or ".otf" or ".fnt" => "symbol-text",
            ".md" or ".markdown" => "markdown",
            ".txt" or ".log" => "book",
            ".json" or ".tmx" or ".tsx" or ".xml" or ".yaml" or ".yml" or ".toml" => "json",
            ".slang" or ".hlsl" or ".glsl" => "symbol-color",
            ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => "symbol-class",
            ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".go" => "code",
            ".ps1" or ".bash" or ".sh" or ".cmd" or ".bat" => "terminal",
            ".zip" or ".7z" or ".gz" or ".tar" => "file-zip",
            ".pdf" => "file-pdf",
            ".exe" or ".dll" or ".pdb" or ".so" or ".dylib" or ".bin" => "file-binary",
            ".wav" or ".ogg" or ".mp3" or ".flac" => "unmute",
            _ => "file"
        };
    }

    /// <summary>
    /// The colour of a file's icon by what it is, the colours the skin writes code in, so a theme colours them too. Null
    /// for the text's own colour, a file of nothing in particular.
    /// </summary>
    private System.Numerics.Vector4? FileIconColor(string path)
    {
        if (compositor.Skin?.Syntax is not { } syntax)
            return null;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".hor" or ".hexspace" => syntax.Declaration,
            ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => syntax.Keyword,
            ".json" or ".yaml" or ".yml" or ".toml" or ".tmx" or ".tsx" or ".xml" => syntax.Call,
            ".md" or ".markdown" or ".txt" => syntax.Property,
            ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" => syntax.Text,
            ".slang" or ".hlsl" or ".glsl" => syntax.Number,
            ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".go" => syntax.Type,
            ".ps1" or ".bash" or ".sh" or ".cmd" or ".bat" => syntax.Builtin,
            _ => null
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
