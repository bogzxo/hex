using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The palette of components on the left of the designer, a button a kind with its icon and what it is, pressed to add one.
internal sealed partial class HexDesigner
{
    internal void BuildPalette()
    {
        // One button per kind of component, each made from the template the palette names
        var items = layout.Populate("palette", Palette.Length);

        for (int i = 0; i < items.Count; i++)
        {
            string kind = Palette[i];
            var button = items[i].Get<Button>("button");

            button.Label = kind.Replace('_', ' ');
            button.Icon = KindIcon(kind);
            button.Tooltip = Describe(kind);
            button.OnPressed = () => AddComponentOf(kind);
            paletteButtons.Add(button);
        }

        // The buttons over the tree, the things done to it most without going up to a menu for them
        void Tool(string name, string tip, Action pressed)
        {
            var button = layout.Get<Button>(name);
            button.Tooltip = tip;
            button.OnPressed = pressed;
        }

        Tool("move_up", "Move up the tree, earlier among its siblings (Ctrl+Up)", () => MoveSelected(-1));
        Tool("move_down", "Move down the tree, later among its siblings (Ctrl+Down)", () => MoveSelected(1));
        Tool("move_out", "Move out of the container it is in (Ctrl+Left)", () => MoveSelectedAcross(inwards: false));
        Tool("move_in", "Move into the container before it (Ctrl+Right)", () => MoveSelectedAcross(inwards: true));
        Tool("duplicate", "Make a copy next to it (Ctrl+D)", DuplicateSelected);
        Tool("remove", "Delete it and everything in it (Del)", DeleteAll);
    }

    /// <summary>What the file on screen can do, at the right end of the tabs.</summary>

    /// <summary>What a kind of component is for, see HidlComponents.</summary>
    internal static string Describe(string kind) => HidlComponents.Describe(kind);

    /// <summary>The icon of a kind of component, see HidlComponents.</summary>
    internal static string KindIcon(string kind) => HidlComponents.KindIcon(kind);
}
