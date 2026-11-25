using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Horizon.Input.Key;

namespace Hex.Dawn;

// The palette of components on the left of the designer, a button a kind with its icon and what it is, pressed to add one.
internal sealed partial class HexDesigner
{
    private bool paletteBuilt;

    /// <summary>Populates the designer component palette once on first Design view.</summary>
    internal void EnsurePalette()
    {
        if (paletteBuilt)
            return;
        BuildPalette();
    }

    internal void BuildPalette()
    {
        if (paletteBuilt)
            return;
        paletteBuilt = true;

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

        // Their tooltips in the strings with the rest of Hex's words
        string Tip(string key) => context.Strings.Said("designer_tools." + key);
        Tool("move_up", Tip("move_up"), () => MoveSelected(-1));
        Tool("move_down", Tip("move_down"), () => MoveSelected(1));
        Tool("move_out", Tip("move_out"), () => MoveSelectedAcross(inwards: false));
        Tool("move_in", Tip("move_in"), () => MoveSelectedAcross(inwards: true));
        Tool("duplicate", Tip("duplicate"), DuplicateSelected);
        Tool("remove", Tip("remove"), DeleteAll);
    }

    /// <summary>What the file on screen can do, at the right end of the tabs.</summary>

    /// <summary>What a kind of component is for, see HidlComponents.</summary>
    internal static string Describe(string kind) => HidlComponents.Describe(kind);

    /// <summary>The icon of a kind of component, see HidlComponents.</summary>
    internal static string KindIcon(string kind) => HidlComponents.KindIcon(kind);
}
