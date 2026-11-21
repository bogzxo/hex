namespace Hex.Dawn;

// Tab moving the keyboard on out of the code rather than typing a tab, VS Code's Ctrl+M. The code keeps Tab for indenting,
// so the keys alone had no way on from it to the rest of the window, a trap for somebody without a mouse. It's for the
// session, the way VS Code has it, every editor of code going the same way.
internal sealed partial class DawnScene
{
    // Whether Tab goes on out of the code, see ToggleTabMovesFocus
    private bool tabMovesFocus;

    /// <summary>Ctrl+M, Tab moving the keyboard on from the code or typing a tab into it.</summary>
    private void ToggleTabMovesFocus()
    {
        tabMovesFocus = !tabMovesFocus;
        foreach (HexDocument open in documents)
            open.Editor.TabMovesFocus = tabMovesFocus;
        Say(tabMovesFocus ? Said("quiet.tab_moves_focus_on") : Said("quiet.tab_moves_focus_off"));
    }
}
