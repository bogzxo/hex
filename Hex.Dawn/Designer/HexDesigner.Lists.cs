using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The designer's lists, the tree of the layout and its layers.
internal sealed partial class HexDesigner
{
    internal void RebuildComponentTree()
    {
        window.UseSideTree(false);
        treeTitle.Text = context.Strings.Said("side.designer");

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
            caption.Color = component.Visible && !component.IsHiddenByLayer ? null : context.Panels.Dim;

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
            window.Hint(tree, "nothing yet, add something\nout of the toolbox above");

        RebuildLayers();
    }

    /* Layers: parts of a layout that are shown and hidden together, so what is laid over something else can be put
       out of the way while that is worked on. Which layer a component is on is one of its properties and goes into
       the file, and so do the ones that are hidden, as the layers the layout starts with hidden (compositor.hidden_layers),
       the screens of a menu that aren't the first, the views of Hex that take turns. */

    internal void RebuildLayers()
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

            caption.Text = hidden ? $"$(eye-closed) {UIRichText.Plain(name)}  (hidden)" : $"$(eye) {UIRichText.Plain(name)}";
            caption.Color = hidden ? context.Panels.Dim : null;

            row.OnPressed = () => ToggleLayer(name);
            layerRows[name] = row;
        }

        if (names.Count == 0)
        {
            window.Hint(layers, "none yet, a component gets one\nin its properties, under layer");
        }
    }

    private void ToggleLayer(string name)
    {
        bool show = document.Module.IsLayerHidden(name);
        document.Module.SetLayerVisible(name, show);

        // The tree greys out what is out of sight, and the code says which layers the layout starts with hidden
        treeDirty = codeDirty = true;
        context.Notices.Say(show ? $"showing the layer {name}" : $"hid the layer {name}, the layout starts with it hidden, everything on it is still in the file");
    }

    private void ShowAllLayers()
    {
        document.Module.ShowAllLayers();
        treeDirty = codeDirty = true;
    }
}
