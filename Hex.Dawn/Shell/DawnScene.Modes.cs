using Hex.Shell;

using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// Dawn and Dusk in the one window. Ctrl+Alt+D (the toggle on the title bar, the View menu, the palette, and the same key
// while a terminal has the keyboard) swaps to Dusk and back, see Hex.Shell.HexModes. The editor is kept whole while the
// terminal is up, its files, its undo and where it was scrolled to, and Dusk the same the other way round. Started on its
// own by something that isn't Hex.exe there's nothing to swap with in the window, and Dusk is started as a program.
internal sealed partial class DawnScene
{
    /// <summary>Kept when it's left for Dusk rather than thrown away, while both modes are in the window.</summary>
    public override bool Persistent => HexModes.Together;

    private Button modeToggle = null!;

    private void BuildModes()
    {
        modeToggle = top.Add(new Button("Dusk")
        {
            Icon = "terminal",
            Tooltip = "Dusk, the terminal, in this window (Ctrl+Alt+D)",
            OnPressed = SwapMode
        });

        terminals.Commands.Register("hex_dusk", "Hex: Dusk, the terminal, in this window", SwapMode, "Ctrl+Alt+D", HexOwns);
        HexModes.Leaving += mode =>
        {
            if (mode == HexMode.Dawn)
                LetGo();
        };
    }

    /// <summary>To Dusk, in the folder that's open, the window's own when Hex.exe started it.</summary>
    private void SwapMode()
    {
        string? folder = explorer.workspace.Folders.Count > 0 ? explorer.workspace.Folders[0] : browseDirectory;
        if (!HexModes.Swap(folder))
            Say("there's no Dusk to swap to", error: true);
    }

    /// <summary>The keyboard given up as the editor is left, or Dusk's terminals never get their keys (there's one keyboard).</summary>
    private void LetGo()
    {
        compositor.Focus = null;
        stage.Focus = null;
        sparks.Clear();
    }

    /// <summary>Back from Dusk, the code given the keyboard again if it's what's showing.</summary>
    public override void OnReturned()
    {
        if (document.View == HexView.Code || document.IsText)
            code.Focus();
        Engine.WindowManager.RequestFrames();
    }
}
