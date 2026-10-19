using Horizon.UI.Components;

namespace Hex.Dusk;

// The commands and the keys, which are the host's (TerminalHost, TerminalCommands), every command a plugin's, keys.hor
// saying which key each is on, the defaults in Assets/dusk/keys.hor and the user's over them. A key pressed in a
// terminal comes through the panel before the shell sees it, one pressed while no terminal has the keyboard is read
// off the keyboard here.
internal sealed partial class DuskScene
{
    /// <summary>Runs a command by name, false when there is none.</summary>
    private bool Run(string name) => host.Run(name);

    /// <summary>The key a command is on as the menus write it, empty for none.</summary>
    private string KeyOf(string name) => host.Commands.KeyOf(name);

    /// <summary>A key pressed in a terminal, before the shell, see TerminalHost.KeyPressed. For the self test.</summary>
    private bool KeyPressed(Horizon.UI.UIKeyPress press) => host.KeyPressed(press);

    // Which commands' keys were down last tick, so a key held goes off once
    private readonly HashSet<TerminalCommand> keysDown = [];

    /// <summary>The keys read off the keyboard while no terminal has them (no tab, or a menu up and gone).</summary>
    private void UpdateKeys()
    {
        var keyboard = Engine.Input.Keyboard;
        bool typing = compositor.Focus is TerminalView or TextBox;
        bool control = keyboard.Control, shift = keyboard.Shift, alt = keyboard.Alt;

        var all = host.Commands.All;
        for (int i = 0; i < all.Count; i++)
        {
            TerminalCommand command = all[i];
            if (command.Parsed is not { } key)
                continue;

            bool down = keyboard.IsDown(key.Key) && control == key.Control && shift == key.Shift && alt == key.Alt;
            bool pressed = down && !keysDown.Contains(command);
            if (down) keysDown.Add(command); else keysDown.Remove(command);

            // A terminal gets its keys through the panel, a text box keeps them to itself
            if (pressed && !typing && chrome.Dialog is null)
                Run(command.Name);
        }
    }
}
