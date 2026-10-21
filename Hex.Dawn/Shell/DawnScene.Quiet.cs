using Silk.NET.Input;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The quiet mode, F11 (Sublime's distraction free, VS Code's zen). The side bar, the panel, the right side, the tabs
// and the breadcrumbs go together, the code or the canvas has the middle, and F11 again puts back what was there and
// nothing else. None of it is written into the settings, it's a moment's way of working and not how Hex is set up.
internal sealed partial class DawnScene
{
    // What was showing when it went quiet, put back as it was
    private (bool Left, bool Right, bool Panel)? quietFrom;
    private StackPanel tabRow = null!;

    private bool Quiet => quietFrom is not null;

    private void BuildQuiet()
    {
        tabRow = layout.Get<StackPanel>("tab_row");
        commands.Bind(Key.F11, () => { if (debugger.DebugStopped) debugger.StepIn(); else ToggleQuiet(); }, whileTyping: true, shift: false);
    }

    /// <summary>Alt+Z, long lines wrapped at the edge of every editor of code or not, kept in the settings.</summary>
    private void ToggleWrap()
    {
        settings.WrapCode = !settings.WrapCode;
        foreach (HexDocument open in documents)
            open.Editor.WordWrap = settings.WrapCode;
        SaveSettings();
        Say(settings.WrapCode ? Said("quiet.wrap_on") : Said("quiet.wrap_off"));
    }

    private void ToggleQuiet()
    {
        if (quietFrom is { } was)
        {
            quietFrom = null;
            if (was.Left) left.Show(true);
            if (was.Right) right.Show(true);
            if (was.Panel) bottom.Show(true);
            tabRow.Visible = crumbs.Visible = true;
            Say(Said("quiet.back"));
            return;
        }

        quietFrom = (left.Shown, right.Shown, bottom.Shown);
        left.Show(false);
        right.Show(false);
        bottom.Show(false);
        tabRow.Visible = crumbs.Visible = false;
        Say(Said("quiet.on"));
    }
}
