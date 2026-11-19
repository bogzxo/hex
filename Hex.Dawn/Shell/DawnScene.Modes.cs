using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

using Key = Horizon.Input.Key;

namespace Hex.Dawn;

// Dawn and Dusk in the one window. Ctrl+Alt+Shift+D (the toggle on the title bar, the View menu, the palette, and the same key
// while a terminal has the keyboard) swaps to Dusk and back, see Hex.Shell.HexModes. The editor is kept whole while the
// terminal is up, its files, its undo and where it was scrolled to, and Dusk the same the other way round. Started on its
// own by something that isn't Hex.exe there's nothing to swap with in the window, and Dusk is started as a program.
internal sealed partial class DawnScene
{
    /// <summary>Kept when it's left for Dusk rather than thrown away, while both modes are in the window.</summary>
    public override bool Persistent => HexModes.Together;

    private Button modeToggle = null!;
    private UIComponent? focusBeforeMode, stageFocusBeforeMode;

    private void BuildModes()
    {
        modeToggle = HexModeSwitch.Add((TitleBar)top, HexFace.Dawn, PickFace);

        terminals.Commands.Register("hex_dusk", Said("modes.command_dusk"), SwapMode, "Ctrl+Alt+Shift+D", HexOwns);
        HexModes.InBackground(HexMode.Dawn, TickAgentsOffscreen);
        if (!HexAi.Off)
            HexAgentProcess.Woken = Engine.WindowManager.Wake;
        HexModes.Leaving += mode =>
        {
            if (mode == HexMode.Dawn)
            {
                FlushZoomSettings();
                LetGo();

                // The editor stops ticking in Dusk, an agent's call that swapped (a click on the toggle) is answered now or
                // it would wait out its whole timeout. One about agents goes on in the background step
                if (mcpCurrent is { NeedsEditor: true } driving)
                {
                    driving.Finish(McpToolResult.Text("Done, and Hex swapped to Dusk, the terminal. The next call brings the editor back."));
                    mcpCurrent = null;
                }
                agentPointer.Release();
            }
        };
    }

    /// <summary>
    /// While Dusk is on screen, the agents go on as they would in the editor. Their output is read, the statuses the
    /// waiting tools look at are made, and the calls that don't need the editor are answered. One that does brings the
    /// editor back, as it always did.
    /// </summary>
    private void TickAgentsOffscreen(float dt)
    {
        // With No AI on there are no agents to read, only the MCP server's calls go on
        if (!HexAi.Off)
        {
            TakeAgentLines();
            PublishAgentStatuses();
        }
        RunMcpCalls(behind: true);

        // A self test that went over to Dusk goes on there, to see what goes on behind it and to come back. Only with Dusk
        // really on screen, a step of the test taking this step by hand would have run itself again inside itself
        if (HexModes.Current == HexMode.Dusk)
            UpdateSelfTest(dt);
    }

    /// <summary>A face picked on the title bar, Dusk swapping the scene and Dawn and Noon trading places in this one.</summary>
    private void PickFace(HexFace face)
    {
        if (face == HexFace.Dusk)
            SwapMode();
        else
            ShowFace(face);
    }

    /// <summary>To Dusk, in the folder that's open, the window's own when Hex.exe started it.</summary>
    private void SwapMode()
    {
        string? folder = explorer.workspace.Folders.Count > 0 ? explorer.workspace.Folders[0] : browseDirectory;
        if (!HexModes.Swap(folder))
            Say(Said("modes.no_dusk"), error: true);
    }

    /// <summary>The keyboard given up as the editor is left, or Dusk's terminals never get their keys (there's one keyboard).</summary>
    private void LetGo()
    {
        focusBeforeMode = compositor.Focus;
        stageFocusBeforeMode = stage.Focus;
        compositor.Focus = null;
        stage.Focus = null;
        sparks.Clear();
    }

    /// <summary>Back from Dusk, the code given the keyboard again if it's what's showing.</summary>
    public override void OnReturned()
    {
        // Noon picked in Dusk comes back as Noon, anything else as the face that was left
        if (HexModes.FaceWanted is { } wanted)
        {
            HexModes.FaceWanted = null;
            ShowFace(wanted);
        }

        if (Face == HexFace.Noon)
            agentsInput.Focus();
        else if (!HexModeSwitch.RestoreFocus(compositor, focusBeforeMode) && (document.View == HexView.Code || document.IsText))
            code.Focus();
        HexModeSwitch.RestoreFocus(stage, stageFocusBeforeMode);

        // Dusk was made bigger or smaller meanwhile, the editor follows so the swap doesn't jump
        if (HexModes.Scale is { } shared && shared != compositor.Scale)
            SetScale(shared, remember: true);
        Engine.WindowManager.RequestFrames();
    }
}
