using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

/// <summary>Which view the side bar shows, picked on the activity bar.</summary>
internal enum HexSide
{
    // The folders of the workspace, or the folder files are opened from without one
    Explorer,

    // What finding in every file (or every use of a name) came to
    Search,

    // What a file of code declares, and the keys of its objects
    Outline,

    // The designer's toolbox, the components of the layout and its layers
    Design
}

// The activity bar down the left and the side bar next to it, VS Code's. A file of code has the explorer, search and
// its outline, the designer of a layout has its toolbox, components and layers instead of the outline, and the explorer
// and search as well. Each remembers which it was left on, so going from the code to the designer and back puts the
// side bar back the way it was.
internal sealed partial class HexScene
{
    private HexSide codeSide = HexSide.Outline, designSide = HexSide.Design;

    // What the side bar was last set up for, so it is only done again when that changes
    private (HexSide Side, bool Tools)? sideShown;

    /// <summary>The view the side bar shows, for what is on screen.</summary>
    private HexSide Side
    {
        get => LayoutTools ? designSide : codeSide;
        set
        {
            if (LayoutTools) designSide = value;
            else codeSide = value;
            treeDirty = true;
        }
    }

    /// <summary>The view of the side bar that is what is on screen unless asked otherwise, the components or the outline.</summary>
    private HexSide HomeSide => LayoutTools ? HexSide.Design : HexSide.Outline;

    /// <summary>
    /// Whether the side bar is showing files. Setting it true is the explorer for code from now on (a folder was
    /// opened), the designer keeps its toolbox unless somebody asks for the explorer there. Setting it false goes back
    /// to the components or the outline.
    /// </summary>
    private bool browsing
    {
        get => Side == HexSide.Explorer;
        set
        {
            if (value)
                codeSide = HexSide.Explorer;
            else
                Side = HomeSide;

            treeDirty = true;
        }
    }

    private void WireActivityBar()
    {
        layout.Get<Button>("act_explorer").OnPressed = () => ShowSide(HexSide.Explorer);
        layout.Get<Button>("act_search").OnPressed = () => ShowSide(HexSide.Search);
        layout.Get<Button>("act_outline").OnPressed = () => ShowSide(HexSide.Outline);
        layout.Get<Button>("act_design").OnPressed = () => ShowSide(HexSide.Design);
        layout.Get<Button>("act_settings").OnPressed = OpenSettings;
    }

    /// <summary>Shows a view of the side bar, bringing the side bar back if it was put away. The view that is showing again puts it away, the way VS Code does.</summary>
    private void ShowSide(HexSide side)
    {
        if (side == HexSide.Outline && LayoutTools) side = HexSide.Design;
        if (side == HexSide.Design && !LayoutTools) side = HexSide.Outline;

        if (Side == side && left.Visible && !options.SelfTest)
        {
            ToggleSideBar();
            return;
        }

        left.Visible = true;
        Side = side;
    }

    private void ToggleSideBar()
    {
        left.Visible = !left.Visible;
        settings.SideBarHidden = !left.Visible;
        settings.Save();
    }

    /// <summary>
    /// Helper method to put on screen what the side bar's view wants, the parts only the designer has and the
    /// activity bar's buttons lit for it. Whenever the view changes, or the document goes between designer and code.
    /// </summary>
    private void ApplySide()
    {
        HexSide side = Side;
        bool tools = LayoutTools;
        if (sideShown == (side, tools))
            return;

        sideShown = (side, tools);
        bool design = tools && side == HexSide.Design;

        foreach (string part in LayoutOnlyParts)
            layout.Get<UIComponent>(part).Visible = design;

        layout.Get<Button>("act_explorer").Selected = side == HexSide.Explorer;
        layout.Get<Button>("act_search").Selected = side == HexSide.Search;
        layout.Get<Button>("act_outline").Selected = side == HexSide.Outline;
        layout.Get<Button>("act_design").Selected = side == HexSide.Design;
        layout.Get<Button>("act_outline").Visible = !tools;
        layout.Get<Button>("act_design").Visible = tools;
        treeScroll.Offset = 0;
    }
}
