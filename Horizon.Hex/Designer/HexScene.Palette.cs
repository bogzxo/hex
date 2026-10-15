using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

// The palette of components on the left of the designer, a button a kind with its icon and what it is, pressed to add one.
internal sealed partial class HexScene
{
    private void BuildPalette()
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

    /// <summary>
    /// Helper method for what a kind of component is for, the line the palette shows when the pointer rests on its button.
    /// </summary>
    private static string Describe(string kind) => kind switch
    {
        "stack" => "A row or a column of things, with a gap between them",
        "grid" => "Cells in rows and columns, so many across",
        "collapsible" => "A stack under a bar with a title, which folds it away",
        "panel" => "A box to put things in, or a backdrop",
        "include" => "Another layout file built in here, its parts yours by name",
        "scroll" => "A panel that scrolls when there is more than fits",
        "label" => "A piece of text, which can wrap",
        "button" => "A button with a label, pressed by a click or the navigator",
        "toggle" => "A box that is ticked or isn't",
        "textbox" => "A line somebody types into",
        "number_box" => "A number, dragged across or typed",
        "selector" => "One of a few options, stepped through with arrows",
        "dropdown" => "One of a list, which drops down",
        "list" => "A list of texts in a box, one chosen",
        "slider" => "A number between two ends, slid along",
        "color_picker" => "A colour, picked by hue, shade and how solid it is",
        "progress_bar" => "How full something is, a health bar say",
        "image" => "A picture, a sprite of the skin or a file",
        "tabs" => "Pages with a strip of tabs along the top",
        "divider" => "A line between things",
        "spacer" => "Nothing, of a size, for a gap",
        "readout" => "A number that changes all the time, and what it is counted in",
        "meter" => "A thin line that says how full something is",
        "graph" => "The last so many of something as bars",
        "segments" => "One bar cut up by how much of a whole things are",
        "symbol" => "One of the icons every skin has, on its own",
        "status_bar" => "The strip along the bottom of an editor, pieces on the left and the right",
        "tab_strip" => "A row of tabs for documents, with an x on each",
        "breadcrumbs" => "Where something is, as a path of pieces",
        _ => string.Empty
    };

    /// <summary>The icon of a kind of component, in the toolbox, the tree and the breadcrumbs.</summary>
    private static string KindIcon(string kind) => kind switch
    {
        "stack" => "list-flat",
        "collapsible" => "fold",
        "grid" => "table",
        "panel" => "window",
        "scroll" => "list-selection",
        "label" => "symbol-text",
        "button" => "symbol-event",
        "toggle" => "check",
        "textbox" => "symbol-string",
        "number_box" => "symbol-number",
        "selector" => "arrow-swap",
        "dropdown" => "chevron-down",
        "list" => "list-unordered",
        "slider" => "settings",
        "color_picker" => "symbol-color",
        "progress_bar" => "loading",
        "image" => "file-media",
        "tabs" => "window",
        "divider" => "dash",
        "spacer" => "whitespace",
        "readout" => "dashboard",
        "meter" => "pulse",
        "graph" => "graph",
        "segments" => "symbol-ruler",
        "menu_bar" => "menu",
        "code" or "code_editor" => "code",
        "group" => "group-by-ref-type",
        "include" => "file-symlink-file",
        "symbol" => "smiley",
        "status_bar" => "info",
        "tab_strip" => "files",
        "breadcrumbs" => "chevron-right",
        _ => "symbol-misc"
    };
}
