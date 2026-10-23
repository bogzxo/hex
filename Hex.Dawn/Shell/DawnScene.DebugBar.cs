namespace Hex.Dawn;

// The debug bar, a little popup over the top middle of the code for as long as something's debugged, the buttons VS Code
// has there. Going on or pausing, stepping over, in and out, starting again and stopping. The steps only do something
// while it's stopped, they're dimmed otherwise. Debugging was keys alone and nothing on screen said which.
internal sealed partial class DawnScene
{
    private const string DEBUG_BAR = "debug_bar.hor";

    private Panel? debugBar;
    private Button? debugContinue;
    private Button[] debugSteps = [];

    // What the bar showed last, whether it was up and whether it was stopped, so it's only changed when that does
    private (bool Up, bool Stopped) debugBarShown;

    private void BuildDebugBar()
    {
        UILayout made = layout.Instantiate(DEBUG_BAR, layout.Get<Panel>("code_scroll"));
        debugBar = made.Get<Panel>("debug_bar");
        debugBar.Visible = false;

        debugContinue = made.Get<Button>("debug_continue");
        debugContinue.OnPressed = debugger.ContinueOrPause;
        Button over = made.Get<Button>("debug_step_over"), into = made.Get<Button>("debug_step_into"), outOf = made.Get<Button>("debug_step_out");
        over.OnPressed = debugger.StepOver;
        into.OnPressed = debugger.StepIn;
        outOf.OnPressed = debugger.StepOut;
        made.Get<Button>("debug_restart").OnPressed = debugger.RestartDebugging;
        made.Get<Button>("debug_stop").OnPressed = debugger.StopDebugging;
        debugSteps = [over, into, outOf];
    }

    /// <summary>Every tick, the bar up while something's debugged, its steps only working while it's stopped.</summary>
    private void UpdateDebugBar()
    {
        if (debugBar is null || debugContinue is null)
            return;

        // Not while the keyboard is in something over the editor (the palette, a dialog), it was drawn on top of them
        bool covered = false;
        for (UIComponent? at = compositor.Focus; at is not null && !covered; at = at.Parent)
            covered = at is UIDialog or WindowPanel;
        var now = (Up: debugger.Debugging && !LayoutTools && !covered, Stopped: debugger.DebugStopped);
        if (now == debugBarShown)
            return;

        debugBarShown = now;
        debugBar.Visible = now.Up;
        debugContinue.Icon = now.Stopped ? "debug-continue" : "debug-pause";
        debugContinue.Tooltip = Said(now.Stopped ? "debug_bar.continue" : "debug_bar.pause");
        foreach (Button step in debugSteps)
            step.Enabled = now.Stopped;
    }
}
