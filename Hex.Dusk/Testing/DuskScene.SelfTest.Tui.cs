using Horizon.UI.Components;

namespace Hex.Dusk;

// The self test of the programs with a screen of their own written in HIDL, each started from its command in the real
// window and typed into the way a key types. The file picker hands a path to the shell it came from, git reads the
// repository Dusk runs in (nothing is staged), the process list finds Dusk, the REPL runs a line. Nothing is ended,
// killed or written.
internal sealed partial class DuskScene
{
    private void TestTui(DuskScript test)
    {
        TerminalView View() => panel.FocusedView!;
        TerminalView? shell = null;
        TerminalTui? Program() => View().Program as TerminalTui;

        test.WaitFor(() => View().Screen.AtPrompt, 10.0f);
        test.Run(() => shell = View());
        test.Check("the programs there are", () => host.Tuis().ToHashSet().IsSupersetOf(["files", "git", "processes", "repl", "settings"]));

        // Files, a path handed to the shell
        test.Run(() => Run("tui_files"));
        test.Settle(settle);
        test.Check("tui_files opens the file picker in a tab of its own", () => Program() is { IsRunning: true, Name: "Files", Failed: null } && panel.Tabs.Count == 2);
        test.Run(() => View().Type("hex.dusk.dll"));
        test.Settle(0.1f);
        test.Run(() => Shot("tui-files"));
        test.Check("typing narrows it down", () => Program()!.ScreenText().Contains("Hex.Dusk.dll") && Program()!.ScreenText().Contains("1 of"));
        test.Run(() => View().Type("\t"));
        test.Settle(settle);
        test.WaitFor(() => shell!.LineText.Contains("Hex.Dusk.dll"), 5.0f);
        test.Check("tab puts the path on the prompt of the shell it came from, and the picker is gone", () =>
            panel.Tabs.Count == 1 && panel.FocusedView == shell && shell!.LineText.Contains("Hex.Dusk.dll"));
        Clear(test);

        // Typed at the prompt, tui on its own says what there is and tui with a name starts it (the shell's own script)
        test.Run(() => View().Send("tui\r"));
        test.WaitFor(() => View().Screen.AtPrompt && View().Screen.Text().Contains("tui processes"), 5.0f);
        test.Check("tui at a prompt says which programs there are and how to start them", () =>
            View().Screen.Text() is { } said && said.Contains("tui processes") && said.Contains("Ctrl+Shift+O"));
        test.Run(() => View().Send("tui processes\r"));
        test.WaitFor(() => Program() is { IsRunning: true, Name: "Processes" }, 5.0f);
        test.Check("and tui with a name starts that one in a tab of its own", () => Program() is { IsRunning: true, Name: "Processes", Failed: null } && panel.Tabs.Count == 2);
        test.Run(() => View().Type("\u001b"));
        test.Run(() => View().Type("\u001b"));
        test.Settle(settle);
        test.WaitFor(() => panel.Tabs.Count == 1 && View().Screen.AtPrompt, 5.0f);
        Clear(test);

        // Git
        test.Run(() => Run("tui_git"));
        test.Settle(settle);
        test.Settle(0.3f);
        test.Run(() => Shot("tui-git"));
        test.Check("tui_git shows the status of the repository the shell is in", () => Program() is { IsRunning: true, Failed: null } program && program.ScreenText().Contains(" Status "));
        test.Run(() => View().Type("\t"));
        test.Run(() => View().Type("\r"));
        test.Settle(0.3f);
        test.Run(() => Shot("tui-git-log"));
        test.Check("and its log, a commit shown beside it (when there is one, Dusk's own repository may have none yet)", () =>
            Program()!.ScreenText() is { } screen && screen.Contains("What it changed") && (!screen.Contains("enter on a commit") || screen.Contains("No commits yet")));
        test.Run(() => View().Type("q"));
        test.Settle(settle);

        // Processes
        test.Run(() => Run("tui_processes"));
        test.Settle(settle);
        test.Run(() => View().Type(System.Diagnostics.Process.GetCurrentProcess().ProcessName));
        test.Settle(0.2f);
        test.Run(() => Shot("tui-processes"));
        test.Check("tui_processes lists what runs, Dusk among it", () => Program() is { Failed: null } program && program.ScreenText().Contains(System.Environment.ProcessId.ToString()));
        test.Run(() => View().Type("\u001b"));
        test.Run(() => View().Type("\u001b"));
        test.Settle(settle);

        // The REPL in a split
        test.Run(() => Run("tui_repl"));
        test.Settle(settle);
        test.Run(() => View().Type("6 * 7\r"));
        test.Run(() => View().Type("mat"));
        test.Settle(0.2f);
        test.Run(() => Shot("tui-repl"));
        test.Check("tui_repl runs a line in a split and lists the names that fit", () =>
            Shape() == "[o|F]" && Program() is { Failed: null } program && program.ScreenText().Contains("\n42") && program.ScreenText().Contains("math"));
        test.Run(() => View().Type("\u001b"));
        test.Run(() => View().Type("\u001b"));
        test.Settle(settle);

        // Settings
        test.Run(() => Run("tui_settings"));
        test.Settle(settle);
        test.Run(() => Shot("tui-settings"));
        test.Check("tui_settings shows the settings as rows", () => Program() is { Failed: null } program && program.ScreenText().Contains("theme") && program.ScreenText().Contains("highlight line"));
        test.Run(() => View().Type("\u001b"));
        test.Settle(settle);
        test.Check("and every one of them leaves cleanly", () => panel.Tabs.Count == 1 && panel.Current!.Tree.Count == 1 && View() == shell);
    }
}
