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
internal sealed partial class DawnScene
{
    /* The command palette */

    /// <summary>
    /// Ctrl+Shift+P, every item of every menu there is right now in a list that narrows as it is typed into, with the
    /// menus it is in and its keys, the way VS Code's does.
    /// </summary>
    private void CommandPalette() =>
        ChooseFrom(Said("palette.title"), Said("palette.message"), PaletteChoices(), recent: RecentCommands);

    /// <summary>
    /// The palette's commands each written as one line, <c>"File: Save   (Ctrl+S)"</c>, the way they were before rows had
    /// parts, for whatever looks one up by what it says.
    /// </summary>
    private List<(string Text, Action Picked)> AllCommands() => [.. PaletteChoices().Select(choice => (choice.Id, choice.Picked))];

    /// <summary>
    /// Every item of every menu that means something right now, and what the extensions added. The menu goes quiet in
    /// front, the keys as keycaps and the item's own icon in front of it all. Each is known by the text it was written as
    /// before rows had parts (UIChoice.Id), so the commands picked last before still go over the rest.
    /// </summary>
    private List<UIChoice> PaletteChoices()
    {
        var commands = new List<UIChoice>();
        foreach (Menu bar in menu.Menus)
        {
            if (bar.IsVisible is { } shown && !shown())
                continue;

            foreach (var (item, path) in bar.Everything())
            {
                if ((item.IsVisible?.Invoke() ?? true) && (item.IsEnabled?.Invoke() ?? true))
                    commands.Add(new UIChoice(item.Label, item.OnPicked!) { Quiet = path, Keys = item.Shortcut, Icon = item.Icon });
            }
        }

        commands.AddRange(UIChooser.Rows([.. extensions.Commands()]).Select(choice => choice with { Icon = "extensions" }));
        commands.AddRange(tasks.AutomationChoices());
        commands.AddRange(UIChooser.Rows(SettingsPaletteCommands()).Select(choice => choice with { Icon = "settings-gear" }));

        // The terminal's commands, the same ones Dusk has, under a heading of their own, a title once (Dusk's palette has why)
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string terminal = Said("palette.terminal");
        foreach (TerminalCommand command in terminals.Commands.All)
        {
            if (!titles.Add(command.Title))
                continue;

            string name = command.Name;
            commands.Add(new UIChoice(HexText.TitleCase(command.Title), () =>
            {
                ShowPanel(CONSOLE_PAGE);
                terminals.Run(name);
            }) { Quiet = terminal, Keys = command.Key, Icon = "terminal" });
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
        Func<char, List<UIChoice>?>? prefixed = null, UIRecentList? recent = null, string typed = "") =>
        ChooseFrom(title, message, UIChooser.Rows(choices), shortestFirst, prefixed, recent, typed);

    /// <summary>The same with rows in their parts, a symbol, a folder or an author dim after, each piece of a commit in its colour.</summary>
    private void ChooseFrom(string title, string message, List<UIChoice> choices, bool shortestFirst = false,
        Func<char, List<UIChoice>?>? prefixed = null, UIRecentList? recent = null, string typed = "")
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        textDialog = UIChooser.Show(chrome, title, message, choices, "@choose.placeholder", shortestFirst, prefixed, recent, typed);
    }

    void IWindows.Choose(string title, string message, List<UIChoice> choices) => ChooseFrom(title, message, choices);
}
