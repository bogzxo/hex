using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using Hex.Shell;

namespace Hex.Dusk;

// What Dusk adds to the terminal object, on top of what every program's has (TerminalHost.CoreApi), and how it answers
// what the host asks of it (ITerminalHostUi), its palette, its chooser, its dialog, and Hex for a file to edit. A
// plugin gets the object as terminal and as dusk, the name it had first.
internal sealed partial class DuskScene : ITerminalHostUi
{
    /// <summary>What only Dusk adds, with what each takes and a line of help, for api.hor.</summary>
    public static readonly TerminalApiFunction[] DuskApiFunctions =
    [
        new("open_in_hex", "path, line?", "Opens a file in Hex at a line, false when there is no Hex."),
        new("quit", "", "Closes Dusk."),
        new("theme", "name?", "The theme, set when a name is given."),
        new("next_theme", "", "The next theme round."),
        new("themes", "", "The names of the themes there are."),
        new("settings", "", "Hex's settings, the one page of them Dawn shows too, over the window."),
        new("quake", "", "Dusk slid down from the top of the screen, or back up out of sight (quake_key in settings.hor does it from anywhere)."),
        new("dawn_mode", "", "Dawn, the editor, in this window in place of the terminal, and back again the same way.")
    ];

    /// <summary>Every name on the terminal object in Dusk, what every program has and what Dusk adds.</summary>
    public static string[] ApiNames => [.. TerminalHost.CoreApiNames, .. DuskApiFunctions.Select(function => function.Name)];

    private IEnumerable<(string, IRuntimeValue)> DuskApi(TerminalPlugin plugin) =>
    [
        ("open_in_hex", Natives.Function("open_in_hex", args => Values.Bool(HexLauncher.Open(args.Text(0), (int)args.Number(1, 0))))),
        ("quit", Natives.Action("quit", _ => Quit(0))),
        ("theme", Natives.Function("theme", args =>
        {
            if (args.Has(0))
                UseTheme(args.Text(0));
            return new StringValue(settings.Theme);
        })),
        ("next_theme", Natives.Action("next_theme", _ => NextTheme())),
        ("themes", Natives.Function("themes", _ => Natives.List(Themes().Select(theme => theme.Name)))),
        ("settings", Natives.Action("settings", _ => ShowSettings())),
        ("quake", Natives.Action("quake", _ => ToggleQuake())),
        ("dawn_mode", Natives.Action("dawn_mode", _ => SwapMode()))
    ];

    /* What the host asks of Dusk */

    void ITerminalHostUi.Palette() => CommandPalette();

    void ITerminalHostUi.Choose(string title, string message, IReadOnlyList<string> items, Action<int> picked)
    {
        var choices = new List<(string Text, Action Picked)>();
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            choices.Add((items[i], () => picked(index)));
        }

        ChooseFrom(title, message, choices);
    }

    void ITerminalHostUi.AskText(string title, string message, string initial, Action<string> answered)
    {
        UIDialog dialog = UIDialog.Show(chrome, title, message, new DialogChoice(HexText.Get("dusk_plugins.ok")), new DialogChoice(HexText.Get("keys.leave_it")));
        var box = dialog.Body.Add(new TextBox { Text = initial, Size = new Vector2(360, 30), TextScale = 0.14f });
        dialog.Buttons[0].OnPressed = () =>
        {
            dialog.Close();
            answered(box.Text);
        };
        box.OnSubmitted = text =>
        {
            dialog.Close();
            answered(text);
        };
        box.Focus();
    }

    /// <summary>What the terminal's dawn command asks for, a file or a folder in the Dawn that's running, or a window of its own.</summary>
    bool ITerminalHostUi.OpenInDawn(string path, bool newWindow)
    {
        bool opened = newWindow ? HexLauncher.Window(path) : Directory.Exists(path) ? HexLauncher.OpenFolder(path) : HexLauncher.Open(path);
        if (opened)
            Say(HexText.Get(newWindow ? "dusk_plugins.dawn_window_on" : "dusk_plugins.opened_in_dawn", ("name", Path.GetFileName(path))));
        return opened;
    }

    /// <summary>A file to edit, in Hex when it can be found (the running one first), in whatever opens it otherwise.</summary>
    bool ITerminalHostUi.OpenFile(string path, int line)
    {
        if (HexLauncher.Open(path, line))
        {
            Say(HexText.Get("dusk_plugins.opened_in_hex", ("name", Path.GetFileName(path))));
            return true;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Say(HexText.Get("dusk_plugins.opened_elsewhere", ("name", Path.GetFileName(path))));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
