using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// The command palette (Ctrl+Shift+P), every item of every menu that means something right now in a list that narrows as
// it is typed into, and the list it's shown in, which whatever else wants a few letters to pick from uses too.
internal sealed partial class HexScene
{
    /* The command palette */

    /// <summary>
    /// Ctrl+Shift+P, every item of every menu there is right now in a list that narrows as it is typed into, with the
    /// menus it is in and its keys, the way VS Code's does.
    /// </summary>
    private void CommandPalette() =>
        ChooseFrom("Command palette", "Everything Hex can do right now, a few letters narrow it down", AllCommands(), recent: RecentCommands);

    /// <summary>Every item of every menu that means something right now, and what the extensions added.</summary>
    private List<(string Text, Action Picked)> AllCommands()
    {
        var commands = new List<(string Text, Action Picked)>();
        foreach (Menu bar in menu.Menus)
        {
            if (bar.IsVisible is { } shown && !shown())
                continue;

            foreach (var (item, path) in bar.Everything())
            {
                if ((item.IsVisible?.Invoke() ?? true) && (item.IsEnabled?.Invoke() ?? true))
                    commands.Add(($"{path}: {item.Label}{(item.Shortcut.Length > 0 ? $"   ({item.Shortcut})" : string.Empty)}", item.OnPicked!));
            }
        }

        commands.AddRange(ExtensionCommands());

        // The terminal's commands, the same ones Dusk has, under a heading of their own
        foreach (TerminalCommand command in terminals.Commands.All)
        {
            string name = command.Name;
            commands.Add(($"Terminal: {command.Title}{(command.Key.Length > 0 ? $"   ({command.Key})" : string.Empty)}", () =>
            {
                ShowPanel(CONSOLE_PAGE);
                terminals.Run(name);
            }));
        }

        return commands;
    }

    /// <summary>
    /// Helper method to ask for one of a list of things, Hex.Shell's chooser (the same Dusk asks with), narrowed down by
    /// what is typed, enter, the first button or a double click picks.
    /// </summary>
    /// <param name="shortestFirst">Whether what is left is put shortest first, the nearest to what was typed for a file's name.</param>
    /// <param name="prefixed">
    /// Other lists for what is typed starting with a character of their own, VS Code's quick open (> for commands, @ for
    /// something in the file), null for the list the dialog started with. The rest of what is typed narrows them down.
    /// </param>
    /// <param name="recent">The ones picked last, over the rest and remembered in it, the palette's commands, quick open's files.</param>
    private void ChooseFrom(string title, string message, List<(string Text, Action Picked)> choices, bool shortestFirst = false,
        Func<char, List<(string Text, Action Picked)>?>? prefixed = null, HexRecentList? recent = null)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        textDialog = Hex.Shell.HexChooser.Show(chrome, title, message, choices, "@choose.placeholder", shortestFirst, prefixed, recent);
    }
}
