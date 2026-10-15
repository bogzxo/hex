using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

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
        ChooseFrom("Command palette", "Everything Hex can do right now, a few letters narrow it down", AllCommands());

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
        return commands;
    }

    /// <summary>
    /// Helper method to ask for one of a list of things in a dialog (choose.hor), narrowed down by what is typed (the
    /// letters in order, the way quick open goes), enter, the first button or a double click picks.
    /// </summary>
    /// <param name="shortestFirst">Whether what is left is put shortest first, the nearest to what was typed for a file's name.</param>
    /// <param name="prefixed">
    /// Other lists for what is typed starting with a character of their own, VS Code's quick open (> for commands, @ for
    /// something in the file), null for the list the dialog started with. The rest of what is typed narrows them down.
    /// </param>
    private void ChooseFrom(string title, string message, List<(string Text, Action Picked)> choices, bool shortestFirst = false,
        Func<char, List<(string Text, Action Picked)>?>? prefixed = null)
    {
        string[] texts = [];
        var byText = new Dictionary<string, Action>();
        char showing = '\uffff';

        // The list that is being narrowed down, the one the dialog started with or another for a prefix
        void Use(char prefix, List<(string Text, Action Picked)> from)
        {
            showing = prefix;
            texts = [.. from.Select(choice => choice.Text)];
            byText.Clear();
            foreach (var (text, picked) in from)
                byText.TryAdd(text, picked);
        }

        Use('\0', choices);

        if (textDialog is { IsOpen: true })
            textDialog.Close();

        ListBox list = null!;
        void PickChosen()
        {
            if (list.Index >= 0 && list.Index < list.Items.Length && byText.TryGetValue(list.Items[list.Index], out Action? picked))
                picked();
        }

        UIDialog shown = textDialog = UIDialog.Show(chrome, title, message, new DialogChoice("Go", PickChosen), new DialogChoice("Cancel"));
        UILayout made = layout.Instantiate(CHOOSE_TEMPLATE, shown.Body);
        TextBox box = made.Get<TextBox>("answer");
        list = made.Get<ListBox>("choices");

        list.Items = texts;
        if (texts.Length > 0) list.Choose(0);

        box.OnChanged = typed =>
        {
            char prefix = typed.Length > 0 && prefixed?.Invoke(typed[0]) is not null ? typed[0] : '\0';
            if (prefix != showing)
                Use(prefix, prefix == '\0' ? choices : prefixed!(prefix)!);

            string query = prefix == '\0' ? typed : typed[1..];
            var left = texts.Where(text => Matches(text, query));
            if (shortestFirst && prefix == '\0')
                left = left.OrderBy(text => Path.GetFileName(text).Length);

            list.Items = [.. left.Take(300)];
            if (list.Items.Length > 0) list.Choose(0);
        };
        box.OnSubmitted = _ =>
        {
            shown.Close();
            PickChosen();
        };

        // The arrows and the pages go up and down the list while the box has the keyboard
        box.OnKey = key =>
        {
            int step = key switch
            {
                UIKeyboard.UP or UIKeyboard.SELECT_UP => -1,
                UIKeyboard.DOWN or UIKeyboard.SELECT_DOWN => 1,
                UIKeyboard.PAGE_UP or UIKeyboard.SELECT_PAGE_UP => -Math.Max(1, list.Rows - 1),
                UIKeyboard.PAGE_DOWN or UIKeyboard.SELECT_PAGE_DOWN => Math.Max(1, list.Rows - 1),
                _ => 0
            };
            if (step != 0 && list.Items.Length > 0)
                list.Choose(Math.Clamp(list.Index + step, 0, list.Items.Length - 1));
        };
        list.OnActivated = _ =>
        {
            shown.Close();
            PickChosen();
        };

        box.Focus();
    }
}
