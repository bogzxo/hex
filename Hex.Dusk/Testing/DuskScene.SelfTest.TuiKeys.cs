using System.Text.RegularExpressions;

using Horizon.Core.Processes;
using Horizon.UI.Components;

namespace Hex.Dusk;

// The arrows pressed for real into a TUI, micro, the whole way a person's go (Windows, the window, the compositor,
// Dusk's keys, the terminal, ConPTY). The micro part hands the terminal its bytes and passed while the arrows did
// nothing for anybody at a keyboard, so this one doesn't. It takes the keyboard, it runs only when asked for by name.
internal sealed partial class DuskScene
{
    private void TestTuiKeys(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;

        string? micro = ProcessEnvironment.Path().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), "micro.exe")).FirstOrDefault(File.Exists);
        TerminalProfile? shell = panel.Profiles.Find(profile => profile.Id is "pwsh" or "powershell");
        if (!RealKeys.Available || micro is null || shell is null)
        {
            test.Check("tuikeys: no micro or no real keys on this machine, nothing to try", () => true);
            return;
        }

        (int Line, int Column) Where()
        {
            var screen = View().Screen;
            for (int y = screen.Grid.Count - 1; y >= 0; y--)
            {
                Match found = Regex.Match(screen.Grid[y].Text(), @"\((\d+),(\d+)\)");
                if (found.Success)
                    return (int.Parse(found.Groups[1].Value), int.Parse(found.Groups[2].Value));
            }

            return (0, 0);
        }

        string folder = Path.Combine(Path.GetTempPath(), "dusk-tuikeys-selftest");
        string config = Path.Combine(folder, "config");
        string file = Path.Combine(folder, "keys.txt");
        test.Run(() =>
        {
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "settings.json"), "{ \"savecursor\": false }");
            File.WriteAllText(file, "one two three\nfour five six\nseven eight nine\n");
            panel.NewTab(shell, folder);
        });
        test.WaitFor(() => View().Profile == shell && View().Screen.MarksSeen && View().Screen.AtPrompt, 15.0f);
        test.Run(() => View().Send($"micro -config-dir \"{config}\" \"{file}\"\r"));
        test.WaitFor(() => Where() == (1, 1), 15.0f);
        test.Settle(0.5f);
        BringToFront(test);

        PressKey(test, RealKeys.RIGHT);
        PressKey(test, RealKeys.RIGHT);
        test.WaitFor(() => Where() == (1, 3), 3.0f);
        test.Check("tuikeys: Right pressed for real moves micro's cursor along", () => Where() == (1, 3));
        PressKey(test, RealKeys.DOWN);
        test.WaitFor(() => Where().Line == 2, 3.0f);
        test.Check("tuikeys: Down pressed for real moves it down", () => Where().Line == 2);
        PressKey(test, RealKeys.UP);
        test.WaitFor(() => Where().Line == 1, 3.0f);
        test.Check("tuikeys: Up pressed for real moves it up", () => Where().Line == 1);
        PressKey(test, RealKeys.LEFT);
        test.WaitFor(() => Where() == (1, 2), 3.0f);
        test.Check("tuikeys: Left pressed for real moves it back", () => Where() == (1, 2));
        test.Check("tuikeys: the keys reached Dusk", () => keysReach && RealKeys.HeldBack == 0);

        test.Run(() => View().Send("\u0011"));
        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Run(() =>
        {
            if (panel.Tabs.Count > 1)
                panel.CloseTab(panel.Current!);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        });
        test.Settle(0.3f);
    }
}
