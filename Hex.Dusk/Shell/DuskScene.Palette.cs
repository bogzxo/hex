using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The command palette (Ctrl+Shift+P), every command there is in a list that narrows as it is typed into, with its key,
// and the chooser it is shown in, which whatever else wants a few letters to pick from (a profile, a theme) uses too.
internal sealed partial class DuskScene
{
    private UIDialog? chooser;

    // The commands used last, over the rest of the palette, kept in Dusk's home (a self test has a home of its own)
    private UIRecentList? recentCommands;
    private UIRecentList RecentCommands => recentCommands ??= new(Path.Combine(TerminalFiles.UserFolder, "recent_commands.txt"));

    /// <summary>Ctrl+Shift+P, every command with its key, a few letters narrow it down.</summary>
    private void CommandPalette()
    {
        // A title once, the first command that has it with its key. Close pane is on two keys, and said twice it read as
        // two things
        var choices = new List<(string Text, Action Picked)>();
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TerminalCommand command in host.Commands.All)
        {
            // Tab 1 to Tab whatever stay on their keys, nobody looks one up in here and they just cluttered the list
            if (IsTabNumber(command.Name))
                continue;

            if (!titles.Add(command.Title))
                continue;

            string name = command.Name;
            choices.Add(($"{command.Title}{(command.Key.Length > 0 ? $"   ({command.Key})" : string.Empty)}", () => Run(name)));
        }

        // Every look there is, Hex's and the user's, a few letters away like everything else (see DuskScene.Looks)
        foreach (Hex.Shell.HexLook look in Hex.Shell.HexLooks.All(new Hex.Core.HexSettings(host.Settings.Path)))
        {
            Hex.Shell.HexLook picked = look;
            choices.Add((HexText.Get("looks.command", ("name", look.Name)), () => PickLook(picked)));
        }

        ChooseFrom("Command palette", "Everything Dusk can do, a few letters narrow it down", choices, RecentCommands);
    }

    /// <summary>Whether a command is one of the core plugin's tab_1, tab_2 and so on, which pick a tab by its number.</summary>
    private static bool IsTabNumber(string name) =>
        name.Length > 4 && name.StartsWith("tab_", StringComparison.Ordinal) && name.Skip(4).All(char.IsAsciiDigit);

    /// <summary>
    /// Helper method to ask for one of a list of things, Hex.Shell's chooser (the same Dawn asks with), narrowed down by
    /// what is typed, enter, the first button or a double click picks.
    /// </summary>
    private void ChooseFrom(string title, string message, List<(string Text, Action Picked)> choices, UIRecentList? recent = null)
    {
        if (chooser is { IsOpen: true })
            chooser.Close();

        chooser = UIChooser.Show(chrome, title, message, choices, recent: recent);
    }
}
