using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>Which view the side bar shows, picked on the activity bar.</summary>
internal enum HexSide
{
    // The folders of the workspace, or the folder files are opened from without one
    Explorer,

    // Finding in every file, its boxes over what it (or every use of a name) came to
    Search,

    // What a file of code declares, and the keys of its objects
    Outline,

    // The designer's toolbox, the components of the layout and its layers
    Design,

    // Debugging, the stack where it stopped, the variables and the breakpoints
    Debug,

    // Git, what's changed in the repositories of the workspace, staged or not, and committing it
    SourceControl
}

// The activity bar down the left and the side bar next to it, VS Code's. A file of code has the explorer, search and
// its outline, the designer of a layout has its toolbox, components and layers instead of the outline, and the explorer
// and search as well. Each remembers which it was left on, so going from the code to the designer and back puts the
// side bar back the way it was.
internal sealed partial class DawnScene
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
        layout.Get<Button>("act_debug").OnPressed = () => ShowSide(HexSide.Debug);
        layout.Get<Button>("act_git").OnPressed = () => ShowSide(HexSide.SourceControl);
        layout.Get<Button>("act_settings").OnPressed = OpenSettings;
        layout.Get<Button>("act_extensions").OnPressed = ShowExtensions;

        // VS Code's keys for its Extensions view, which is the settings' Extensions here
        commands.Add("View/sides", Said("activity.extensions_command"), ShowExtensions, "Ctrl+Shift+X", "extensions", UIKeyUse.BoundWhileTyping);
    }

    // Whether the settings are up on Extensions because the activity bar's button asked for them, which is lit while they are
    private bool extensionsUp;

    /// <summary>The extensions, the settings window on its Extensions, the button lit for as long as that's up.</summary>
    private void ShowExtensions()
    {
        OpenSettings(EXTENSIONS);
        extensionsUp = SettingsWindow is not null;
        sideShown = null;
        ApplySide();
    }

    /// <summary>Every tick, the Extensions button let go of once the window it lit for has closed.</summary>
    private void FollowExtensions()
    {
        if (!extensionsUp || SettingsWindow is not null)
            return;

        extensionsUp = false;
        sideShown = null;
        ApplySide();
    }

    /// <summary>Shows a view of the side bar, bringing the side bar back if it was put away. The view that is showing again puts it away, the way VS Code does.</summary>
    private void ShowSide(HexSide side)
    {
        if (side == HexSide.Outline && LayoutTools) side = HexSide.Design;
        if (side == HexSide.Design && !LayoutTools) side = HexSide.Outline;

        // A view of the side asked for takes the light back from Extensions, whose window can stay up, and the view that
        // was under it is lit again rather than put away
        bool fromExtensions = extensionsUp;
        if (fromExtensions)
        {
            extensionsUp = false;
            sideShown = null;
            treeDirty = true;
        }

        if (Side == side && left.Shown && !options.SelfTest && !fromExtensions)
        {
            ToggleSideBar();
            return;
        }

        left.Show(true);
        Side = side;
        FocusSideList();
    }

    /// <summary>
    /// Helper method to give the keyboard to the list of a side that was just shown, the explorer, Source Control or
    /// what a search found, so Up, Down and Enter go down it at once the way VS Code focuses the view. Ctrl+Shift+G left
    /// the keyboard in the code and a change could only be opened with a click. The list is made there and then rather
    /// than at the end of the update, it has to be on screen to be given the keyboard.
    /// </summary>
    private void FocusSideList()
    {
        if (Side is not (HexSide.Explorer or HexSide.SourceControl or HexSide.Search))
            return;

        RebuildTree();
        treeDirty = false;
        if (!sideTree.Visible)
            return;

        // Search gives its box the keys, the way VS Code's view does, Down from there isn't how a search starts
        if (Side == HexSide.Search && searchView is { Visible: true })
        {
            FocusSearchBox();
            return;
        }

        // With nothing picked yet the first open row with rows under it is, Changes, a workspace folder or a file found
        // in, so the first Down lands on the first thing in it. Down from nothing picked was Source Control's Commit all,
        // and Enter after it committed every change
        sideTree.Selected ??= sideTree.Items.Find(item => item.HasChildren && item.Expanded);
        sideTree.Focus();
    }

    private void ToggleSideBar()
    {
        left.ToggleShown();
        settings.SideBarHidden = !left.Shown;
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

        // One button lit at a time, Extensions while its window is up from it and the side's own otherwise
        bool sides = !extensionsUp;
        layout.Get<Button>("act_explorer").Selected = sides && side == HexSide.Explorer;
        layout.Get<Button>("act_search").Selected = sides && side == HexSide.Search;
        layout.Get<Button>("act_outline").Selected = sides && side == HexSide.Outline;
        layout.Get<Button>("act_design").Selected = sides && side == HexSide.Design;
        layout.Get<Button>("act_debug").Selected = sides && side == HexSide.Debug;
        layout.Get<Button>("act_git").Selected = sides && side == HexSide.SourceControl;
        layout.Get<Button>("act_extensions").Selected = extensionsUp;
        layout.Get<Button>("act_outline").Visible = !tools;
        layout.Get<Button>("act_design").Visible = tools;
        if (refreshButton is not null)
            refreshButton.Visible = side == HexSide.Explorer;
        treeScroll.Offset = 0;
    }
}
